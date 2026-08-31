namespace Yemekhane.UnitTests.Persistence;

/// <summary>
/// SQLite havuz temizliği process genelinde etkili olduğu için bu koleksiyon tek başına çalışır.
/// </summary>
[CollectionDefinition(LocalDatabaseTests.CollectionName, DisableParallelization = true)]
public sealed class LocalDatabaseTestGroup;
