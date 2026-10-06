# Copyright (c) 2026 Dennis Liu. All rights reserved.
"""Fetch and verify the Core Release assets pinned in a tool's manifest."""

import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import ssl
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


MAX_PACKAGE_BYTES = 256 * 1024 * 1024
CHUNK_BYTES = 64 * 1024


class FetchError(Exception):
    """A package could not be downloaded or verified."""


class HttpsOnlyRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urllib.parse.urlsplit(newurl).scheme != "https":
            raise FetchError(f"download failed: {req.full_url}: refused non-HTTPS redirect to {newurl}")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def load_manifest(path):
    with path.open(encoding="utf-8") as stream:
        manifest = json.load(stream)
    if not isinstance(manifest, dict):
        raise ValueError("manifest must be a JSON object")
    if set(manifest) != {"schema", "repository", "packages"}:
        raise ValueError("manifest requires only schema, repository, and packages")
    if type(manifest["schema"]) is not int or manifest["schema"] != 1:
        raise ValueError("schema must be 1")
    repository = manifest["repository"]
    if not isinstance(repository, str) or not re.fullmatch(
        r"[A-Za-z0-9_-]+/[A-Za-z0-9_][A-Za-z0-9_.-]*", repository
    ):
        raise ValueError("repository must look like owner/name")
    if not isinstance(manifest["packages"], list):
        raise ValueError("packages must be an array")
    assets = set()
    for package in manifest["packages"]:
        if not isinstance(package, dict):
            raise ValueError("each package must be a JSON object")
        context = f"{package.get('asset', '<unknown asset>')} ({package.get('release', '<unknown Release>')})"
        if set(package) != {"release", "asset", "sha256"}:
            raise ValueError(f"{context}: package requires only release, asset, and sha256")
        for key in ("release", "asset"):
            value = package[key]
            if (
                not isinstance(value, str)
                or not re.fullmatch(r"[A-Za-z0-9._-]+", value)
                or ".." in value
            ):
                raise ValueError(f"{context}: invalid {key}")
        if not package["asset"].endswith(".nupkg"):
            raise ValueError(f"{context}: asset must end with .nupkg")
        # The Release tag ends with -v<version>, and the asset name ends with .<version>.nupkg.
        tag_version = re.fullmatch(r".+-v(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)", package["release"])
        if tag_version is None or not package["asset"].endswith(f".{tag_version.group(1)}.nupkg"):
            raise ValueError(f"{context}: Release tag and asset name must give the same version")
        if not isinstance(package["sha256"], str) or not re.fullmatch(
            r"[0-9a-f]{64}", package["sha256"]
        ):
            raise ValueError(f"{context}: sha256 must be 64 lowercase hexadecimal characters")
        if package["asset"] in assets:
            raise ValueError(f"{context}: duplicate asset")
        assets.add(package["asset"])
    return manifest


def file_sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(CHUNK_BYTES), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download_package(opener, url, package, destination):
    for attempt in range(3):
        temporary = None
        try:
            with opener.open(url, timeout=60) as response:
                header = response.headers.get("Content-Length")
                try:
                    length = int(header) if header is not None else None
                except ValueError as error:
                    raise FetchError(f"download failed: {url}: invalid Content-Length") from error
                if length is not None and (length < 0 or length > MAX_PACKAGE_BYTES):
                    raise FetchError(f"download failed: {url}: response exceeds {MAX_PACKAGE_BYTES} byte limit")
                digest = hashlib.sha256()
                size = 0
                with tempfile.NamedTemporaryFile(
                    mode="wb", dir=destination.parent,
                    prefix=f".{package['asset']}.", suffix=".tmp", delete=False,
                ) as stream:
                    temporary = Path(stream.name)
                    while True:
                        chunk = response.read(CHUNK_BYTES)
                        if not chunk:
                            break
                        size += len(chunk)
                        if size > MAX_PACKAGE_BYTES:
                            raise FetchError(f"download failed: {url}: response exceeds {MAX_PACKAGE_BYTES} byte limit")
                        stream.write(chunk)
                        digest.update(chunk)
                if length is not None and size < length:
                    raise http.client.IncompleteRead(b"", length - size)
                actual = digest.hexdigest()
                if actual != package["sha256"]:
                    raise FetchError(
                        f"SHA-256 mismatch: expected {package['sha256']}, actual {actual}"
                    )
            os.replace(temporary, destination)
            temporary = None
            return
        except (urllib.error.URLError, http.client.HTTPException, TimeoutError, ConnectionError, ssl.SSLError) as error:
            if attempt == 2:
                raise FetchError(f"download failed: {url}: {error}") from error
        except OSError as error:
            raise FetchError(f"download failed: {url}: {error}") from error
        finally:
            if temporary is not None:
                temporary.unlink()
        time.sleep(0.2 * (attempt + 1))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", default="core-packages.json")
    parser.add_argument("--dest", default="artifacts/core-packages")
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--base-url", help="replace https://github.com for tests")
    args = parser.parse_args(argv)
    try:
        manifest_path = Path(args.manifest).resolve()
        manifest = load_manifest(manifest_path)
        base = args.base_url if args.base_url is not None else "https://github.com"
        parsed = urllib.parse.urlsplit(base)
        parsed.port  # Validate malformed ports before any request.
        if (
            parsed.scheme not in {"http", "https"} or not parsed.netloc
            or parsed.username is not None or parsed.password is not None
            or parsed.query or parsed.fragment
        ):
            raise ValueError("base URL must be an HTTP or HTTPS URL without credentials, query, or fragment")
        destination = Path(args.dest)
        if not destination.is_absolute():
            destination = manifest_path.parent / destination
        destination = destination.resolve()
    except (ValueError, OSError) as error:
        print(f"core-packages: manifest or usage error: {error}", file=sys.stderr)
        return 2

    opener = urllib.request.build_opener(
        *([HttpsOnlyRedirectHandler()] if args.base_url is None else [])
    )
    for package in manifest["packages"]:
        context = f"{package['asset']} ({package['release']})"
        target = destination / package["asset"]
        url = f"{base.rstrip('/')}/{manifest['repository']}/releases/download/{package['release']}/{package['asset']}"
        try:
            if target.exists():
                actual = file_sha256(target)
                if actual == package["sha256"]:
                    print(f"core-packages: {context}: verified")
                    continue
                if args.offline:
                    raise FetchError(
                        f"offline verification failed: {target}: SHA-256 mismatch: "
                        f"expected {package['sha256']}, actual {actual}"
                    )
                print(
                    f"core-packages: warning: {context}: {target}: SHA-256 mismatch: "
                    f"expected {package['sha256']}, actual {actual}. Downloading replacement",
                    file=sys.stderr,
                )
            elif args.offline:
                raise FetchError(f"offline verification failed: missing {target}")
            destination.mkdir(parents=True, exist_ok=True)
            download_package(opener, url, package, target)
            print(f"core-packages: {context}: downloaded and verified")
        except (FetchError, OSError) as error:
            print(f"core-packages: {context}: {error}", file=sys.stderr)
            return 1
    print(f"core-packages: {destination}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
