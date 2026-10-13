# EventImplementedInduceInterface

An experimental C# project for event-driven, asynchronous worker processing.

## Project status

**The current implementation (Eiii, Phase 2) is on the [`refactor/phase2`](https://github.com/shirothin/EventImplementedInduceInterface/tree/refactor/phase2) branch.**

This `master` branch keeps the original implementation from 2018 for historical reference. Its source code is not maintained.

The `refactor/phase2` branch targets .NET 10 and contains the reworked library, samples, tests, and documentation (written in Japanese). It redesigns the worker lifecycle, cancellation and completion handling, and the launcher API.

> **Note:** Phase 2 is still under development and has no release yet. It introduces breaking API and behavioral changes, and it is not a drop-in replacement for the original implementation.

## License

MIT License. See [LICENSE](LICENSE).
