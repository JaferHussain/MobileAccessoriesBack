using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Time;
using MoizPos.Domain.Errors;
using MoizPos.Infrastructure.Storage;

namespace MoizPos.Api.Controllers;

/// <summary>
/// The proof behind every non-cash transaction — a sale, a recovery, a supplier payment, a refund
/// or a bank expense. One screenshot each, attached after the transaction is saved and never
/// blocking it.
///
/// <para><b>Authenticated, always.</b> A proof is internal evidence and is never served by the
/// public receipt link or as a static file — it lives outside wwwroot and leaves only through
/// here. Sales, recoveries and refunds are open to any signed-in user (the salesman takes that
/// money); supplier payments and expenses are the owner's alone.</para>
/// </summary>
[ApiController]
[Route("api/proofs")]
[Authorize]
public sealed class ProofsController : ControllerBase
{
    private readonly ITransactionProofRepository _proofs;
    private readonly IImageStorageService _images;
    private readonly PeriodResolver _periods;

    public ProofsController(
        ITransactionProofRepository proofs, IImageStorageService images, PeriodResolver periods)
    {
        _proofs = proofs;
        _images = images;
        _periods = periods;
    }

    /// <summary>
    /// Every non-cash transaction still without a proof, newest first — the owner's daily list to
    /// chase. <paramref name="from"/> and <paramref name="to"/> are shop-local days, inclusive.
    /// </summary>
    [HttpGet("missing")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Missing(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var fromUtc = from is { } first ? _periods.ResolveLocalDateRange(first, first).StartUtc : (DateTime?)null;
        var toUtc = to is { } last ? _periods.ResolveLocalDateRange(last, last).EndUtc : (DateTime?)null;

        var rows = await _proofs.MissingAsync(fromUtc, toUtc, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<MissingProofRow>>.Ok(rows));
    }

    [HttpPost("{kind}/{id:long}")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> Attach(
        string kind, long id, IFormFile file, CancellationToken cancellationToken)
    {
        if (!IsAllowed(kind, out var proofKind))
        {
            return Forbid();
        }

        var target = await _proofs.FindAsync(proofKind, id, cancellationToken)
            ?? throw new NotFoundException(TransactionProofRules.Slug(proofKind), id);

        // Cash is its own proof; a return that refunded nothing has nothing to prove.
        if (TransactionProofRules.RefusalFor(proofKind, target.Method) is { } refusal)
        {
            throw new BusinessRuleViolationException(refusal);
        }

        if (file is null || file.Length == 0)
        {
            throw new BusinessRuleViolationException("No proof was uploaded.");
        }

        await using var stream = file.OpenReadStream();

        var relativePath = await _images.SavePaymentProofAsync(
            stream, file.ContentType, file.Length, cancellationToken);

        await _proofs.SetProofPathAsync(proofKind, id, relativePath, cancellationToken);

        // Only once the new one is safely stored and recorded: a replacement that deleted first
        // and then failed would leave the transaction with no proof at all.
        if (!string.IsNullOrWhiteSpace(target.ProofPath))
        {
            _images.DeletePaymentProof(target.ProofPath);
        }

        return Ok(ApiResponse<object>.Ok(new { kind = TransactionProofRules.Slug(proofKind), id, hasProof = true }));
    }

    /// <summary>The screenshot itself, full size — opened in a dispute or not at all.</summary>
    [HttpGet("{kind}/{id:long}")]
    public async Task<IActionResult> View(string kind, long id, CancellationToken cancellationToken)
    {
        if (!IsAllowed(kind, out var proofKind))
        {
            return Forbid();
        }

        var target = await _proofs.FindAsync(proofKind, id, cancellationToken)
            ?? throw new NotFoundException(TransactionProofRules.Slug(proofKind), id);

        var opened = target.ProofPath is null ? null : _images.OpenPaymentProof(target.ProofPath);

        if (opened is not { } proof)
        {
            throw new NotFoundException("Proof", id);
        }

        return File(proof.Content, proof.ContentType);
    }

    /// <summary>
    /// An unknown kind is a 404, never a guess. A salesman asking for the owner's kinds — supplier
    /// payments, expenses — is refused (403) before anything is read.
    /// </summary>
    private bool IsAllowed(string kind, out ProofKind proofKind)
    {
        if (!TransactionProofRules.TryParse(kind, out proofKind))
        {
            throw new NotFoundException("Kind of proof", kind);
        }

        return !TransactionProofRules.IsAdminOnly(proofKind) || CurrentUser.IsAdmin(User);
    }
}
