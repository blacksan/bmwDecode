// ReferenceData.Pack and Translator.Current are process-wide: tests that set them must not overlap with tests
// that read them (type key loading, fault parsing). The whole suite runs in about a second, so run it serially.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
