# MVP evidence boundaries and deferrals

The MVP supports decoded-I2C sources (NDS communication log, Saleae, KingstVIS, DSL, Excel, and canonical text), Common 0x82–0x85, and Desay 0x97 with explicit Standard/Benz Palm selection. It does not infer Event Buffer Version or Benz Palm, and it never rewrites captured bytes.

The following remain deliberately gated rather than guessed:

- Acute source support until a real decoder export and reviewed definition are available;
- raw waveform-to-I2C decoding until representative LA raw exports establish edge, timing, ACK, and repeated-start behavior;
- semantic Linux/Android kernel parsing until a real capture grammar is reviewed (PID1615 remains a future reference, not executable specification);
- executable QA rule imports until the QA records are scoped and approved for conversion;
- multi-source clock alignment/comparison until clock provenance and drift requirements are measured;
- persistent disk cache until large-capture profiling demonstrates that the in-memory/sparse-checkpoint design is insufficient.

Private captures, firmware BINs, customer golden data, and confidential QA evidence are never bundled. The release has no telemetry, upload path, automatic update check, or background network dependency. FFmpeg is intentionally not bundled; MP4 export uses an operator-selected executable and otherwise produces an atomic deterministic PNG sequence.
