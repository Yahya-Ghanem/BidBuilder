using FluentValidation;
using BidBuilder.Api.Endpoints;

namespace BidBuilder.Api.Validation;

/// <summary>
/// Validators for the highest-value write DTOs (19.7). Each one captures the same
/// invariants the handlers used to check ad-hoc (negative money, out-of-range
/// percentage, mis-ordered dates) but now in one consistent place and with proper
/// field-level error messages.
///
/// Rule of thumb for what belongs here vs. in the handler:
///   • Cheap, decision-free checks (shape, range, format) → here.
///   • Anything that needs the DB or other services (uniqueness, FK existence,
///     concurrency, permission) → stays in the handler.
/// </summary>
public sealed class BidOutcomeRequestValidator : AbstractValidator<BidOutcomeRequest>
{
    public BidOutcomeRequestValidator()
    {
        RuleFor(x => x.SubmittedBidValue).GreaterThanOrEqualTo(0m)
            .When(x => x.SubmittedBidValue.HasValue)
            .WithMessage("Submitted bid value cannot be negative.");
        RuleFor(x => x.AwardedValue).GreaterThanOrEqualTo(0m)
            .When(x => x.AwardedValue.HasValue)
            .WithMessage("Awarded value cannot be negative.");
        RuleFor(x => x.FinalCost).GreaterThanOrEqualTo(0m)
            .When(x => x.FinalCost.HasValue)
            .WithMessage("Final cost cannot be negative.");
        RuleFor(x => x.WinLossNote).MaximumLength(1000)
            .When(x => !string.IsNullOrEmpty(x.WinLossNote))
            .WithMessage("Win/loss note must be 1000 characters or fewer.");
    }
}

public sealed class LaborInputValidator : AbstractValidator<LaborInput>
{
    public LaborInputValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.RatePerHour).GreaterThanOrEqualTo(0m)
            .WithMessage("Rate per hour cannot be negative.");
    }
}

public sealed class MaterialInputValidator : AbstractValidator<MaterialInput>
{
    public MaterialInputValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Unit).NotEmpty().MaximumLength(16);
        RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0m)
            .WithMessage("Unit price cannot be negative.");
        RuleFor(x => x.WastagePct).InclusiveBetween(0m, 100m)
            .WithMessage("Wastage % must be between 0 and 100.");
    }
}

public sealed class EquipmentInputValidator : AbstractValidator<EquipmentInput>
{
    public EquipmentInputValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.RatePerHour).GreaterThanOrEqualTo(0m)
            .WithMessage("Rate per hour cannot be negative.");
    }
}

public sealed class SubcontractorInputValidator : AbstractValidator<SubcontractorInput>
{
    public SubcontractorInputValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Unit).NotEmpty().MaximumLength(16);
        RuleFor(x => x.UnitRate).GreaterThanOrEqualTo(0m)
            .WithMessage("Unit rate cannot be negative.");
    }
}

public sealed class RateHistoryInputValidator : AbstractValidator<RateHistoryInput>
{
    public RateHistoryInputValidator()
    {
        RuleFor(x => x.Rate).GreaterThanOrEqualTo(0m)
            .WithMessage("Rate cannot be negative.");
        RuleFor(x => x.Source).MaximumLength(200)
            .When(x => !string.IsNullOrEmpty(x.Source));
    }
}

public sealed class QuoteInputValidator : AbstractValidator<QuoteInput>
{
    public QuoteInputValidator()
    {
        RuleFor(x => x.Supplier).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Currency).NotEmpty().Length(3).WithMessage("Currency must be a 3-letter ISO code.");
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0m)
            .WithMessage("Price cannot be negative.");
        RuleFor(x => x).Must(x => x.ValidUntil is null || x.ValidUntil >= x.QuotedOn)
            .WithMessage("ValidUntil cannot be before QuotedOn.")
            .OverridePropertyName(nameof(QuoteInput.ValidUntil));
        RuleFor(x => x.Unit).MaximumLength(16);
        RuleFor(x => x.Note).MaximumLength(400).When(x => !string.IsNullOrEmpty(x.Note));
        RuleFor(x => x.AttachmentUrl).MaximumLength(500).When(x => !string.IsNullOrEmpty(x.AttachmentUrl));
    }
}
