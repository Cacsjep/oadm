// One Avalonia headless session per test process and Avalonia objects only on its thread (CLAUDE.md "tests that never
// flake"): the view-model tests and the headless tests share the process, so the tests run one after another, like
// Oadm.Client.Tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
