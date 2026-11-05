using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stackarr.Core;

/// <summary>
/// Non-generic marker for EF Core entity configurations contributed by feature modules.
/// Implement this alongside <see cref="IEntityTypeConfiguration{TEntity}"/> so the shared
/// <see cref="StackarrDbContext"/> can discover all module-owned entities without depending on them.
/// </summary>
public interface IModuleEntityConfiguration
{
    void Configure(ModelBuilder modelBuilder);
}

/// <summary>
/// Base class for module entity configurations. Inherit from this and implement
/// <see cref="IEntityTypeConfiguration{TEntity}.Configure(EntityTypeBuilder{TEntity})"/>.
/// </summary>
public abstract class ModuleEntityConfiguration<TEntity> : IModuleEntityConfiguration, IEntityTypeConfiguration<TEntity>
    where TEntity : class
{
    public abstract void Configure(EntityTypeBuilder<TEntity> builder);

    void IModuleEntityConfiguration.Configure(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfiguration(this);
}
