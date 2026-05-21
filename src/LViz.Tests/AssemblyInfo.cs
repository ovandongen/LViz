using Xunit;

// MainWindowViewModel writes to LayerColorPalette (static) on construction,
// which races with palette-sensitive assertions when test classes run in
// parallel. Run them sequentially — the suite is small (≈100ms total).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
