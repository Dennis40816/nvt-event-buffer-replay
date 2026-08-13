# ADR 0002: Separate source detection from format interpretation

Status: accepted.

Source adapters identify and normalize file grammars. Format descriptors
interpret logical Event Buffer payloads. Source probing may rank candidates
automatically; Event Buffer family/version and Desay Palm profile remain
explicit semantic configuration.

This prevents transport facts such as an `0x01` slave address or 80-byte count
from silently selecting Common 0x83. Unknown input can open in Raw Explorer
before semantic configuration is complete.
