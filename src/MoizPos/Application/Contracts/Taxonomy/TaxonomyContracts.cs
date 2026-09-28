using FluentValidation;

namespace MoizPos.Application.Contracts.Taxonomy;

/// <summary>A category as it is listed and edited.</summary>
public sealed record CategoryDto
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public bool IsActive { get; init; }

    /// <summary>How many products are filed here. Drives the "in use" warning before removal.</summary>
    public int ProductCount { get; init; }
}

public sealed record BrandDto
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>True for a locally made brand, false for an imported one (FR-087a).</summary>
    public bool IsLocal { get; init; }

    public bool IsActive { get; init; }

    public int ProductCount { get; init; }
}

public sealed record CategoryUpsertRequest
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }
}

public sealed record BrandUpsertRequest
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>
    /// Omitted means Imported. A brand is never assumed local — the owner has to say so, or stock
    /// would be misreported under the Local filter (FR-087a).
    /// </summary>
    public bool IsLocal { get; init; }
}

public sealed class CategoryUpsertValidator : AbstractValidator<CategoryUpsertRequest>
{
    public CategoryUpsertValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Category name is required.")
            .MaximumLength(80).WithMessage("Category name cannot exceed 80 characters.");

        RuleFor(x => x.Description)
            .MaximumLength(255).WithMessage("Description cannot exceed 255 characters.");
    }
}

public sealed class BrandUpsertValidator : AbstractValidator<BrandUpsertRequest>
{
    public BrandUpsertValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Brand name is required.")
            .MaximumLength(80).WithMessage("Brand name cannot exceed 80 characters.");

        RuleFor(x => x.Description)
            .MaximumLength(255).WithMessage("Description cannot exceed 255 characters.");
    }
}
