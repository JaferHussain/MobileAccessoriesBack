using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Domain.Errors;
using MoizPos.Infrastructure.Storage;

namespace MoizPos.Api.Controllers;

/// <summary>
/// Udhaar customers — the people the shop gives credit to, registered by the owner with name,
/// phone and both sides of the ID card. <b>Owner only</b>, every route: registering is the decision
/// to give credit, and an ID card is a customer's identity document.
///
/// <para>The photos live in the private proof directory and leave only through
/// <c>GET {id}/id-card/{side}</c>. No other response carries even their path.</para>
/// </summary>
[ApiController]
[Route("api/udhaar-customers")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class UdhaarCustomersController : ControllerBase
{
    /// <summary>Two photos from a phone camera, each within the image limit, plus the form fields.</summary>
    private const long FormLimitBytes = 10 * 1024 * 1024;

    private readonly IUdhaarCustomerRepository _udhaar;
    private readonly IImageStorageService _images;

    public UdhaarCustomersController(IUdhaarCustomerRepository udhaar, IImageStorageService images)
    {
        _udhaar = udhaar;
        _images = images;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<UdhaarCustomerRow>>.Ok(await _udhaar.ListAsync(search, cancellationToken)));

    /// <summary>Any customer's udhaar standing — what their ledger page needs to offer the right button.</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Status(long id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<UdhaarCustomerRow>.Ok(await FindAsync(id, cancellationToken)));

    /// <summary>Registers a new udhaar customer: name, phone, ID card front and back — all required.</summary>
    [HttpPost]
    [RequestSizeLimit(FormLimitBytes)]
    public async Task<IActionResult> Register(
        [FromForm] string? name,
        [FromForm] string? mobileNumber,
        IFormFile? idCardFront,
        IFormFile? idCardBack,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleViolationException("Enter the customer's name.");
        }

        if (string.IsNullOrWhiteSpace(mobileNumber))
        {
            throw new BusinessRuleViolationException("Enter the customer's phone number — it is how the udhaar is collected.");
        }

        if (idCardFront is null || idCardBack is null)
        {
            throw new BusinessRuleViolationException("Add a photo of both sides of the customer's ID card.");
        }

        var front = await SaveAsync(idCardFront, cancellationToken);
        string back;

        try
        {
            back = await SaveAsync(idCardBack, cancellationToken);
        }
        catch
        {
            _images.DeletePaymentProof(front);
            throw;
        }

        var id = await _udhaar.CreateAsync(name.Trim(), mobileNumber.Trim(), front, back, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<UdhaarCustomerRow>.Ok(await FindAsync(id, cancellationToken)));
    }

    /// <summary>
    /// Makes an existing customer an udhaar customer — from their ledger's "Make udhaar customer" —
    /// or completes the ID card of one marked before cards were asked for. A side already on file
    /// need not be sent again; one that is sent replaces it.
    /// </summary>
    [HttpPost("{id:long}/register")]
    [RequestSizeLimit(FormLimitBytes)]
    public async Task<IActionResult> RegisterExisting(
        long id,
        [FromForm] string? mobileNumber,
        IFormFile? idCardFront,
        IFormFile? idCardBack,
        CancellationToken cancellationToken)
    {
        var current = await FindAsync(id, cancellationToken);
        var (oldFront, oldBack) = await _udhaar.IdCardPathsAsync(id, cancellationToken);

        if (string.IsNullOrWhiteSpace(mobileNumber) && string.IsNullOrWhiteSpace(current.MobileNumber))
        {
            throw new BusinessRuleViolationException("Enter the customer's phone number — it is how the udhaar is collected.");
        }

        if ((idCardFront is null && oldFront is null) || (idCardBack is null && oldBack is null))
        {
            throw new BusinessRuleViolationException("Add a photo of both sides of the customer's ID card.");
        }

        var front = idCardFront is null ? null : await SaveAsync(idCardFront, cancellationToken);
        var back = idCardBack is null ? null : await SaveAsync(idCardBack, cancellationToken);

        await _udhaar.RegisterAsync(
            id, string.IsNullOrWhiteSpace(mobileNumber) ? null : mobileNumber.Trim(), front, back, cancellationToken);

        // A replaced photo nothing points at any more would be unreachable forever.
        if (front is not null && oldFront is not null)
        {
            _images.DeletePaymentProof(oldFront);
        }

        if (back is not null && oldBack is not null)
        {
            _images.DeletePaymentProof(oldBack);
        }

        return Ok(ApiResponse<UdhaarCustomerRow>.Ok(await FindAsync(id, cancellationToken)));
    }

    /// <summary>Takes the udhaar mark away. What they owe is untouched; the ID card is kept as evidence.</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Remove(long id, CancellationToken cancellationToken)
    {
        _ = await FindAsync(id, cancellationToken);
        await _udhaar.RemoveAsync(id, cancellationToken);

        return Ok(ApiResponse<UdhaarCustomerRow>.Ok(await FindAsync(id, cancellationToken)));
    }

    /// <summary>One side of the ID card, streamed — never a link to the file.</summary>
    [HttpGet("{id:long}/id-card/{side}")]
    public async Task<IActionResult> IdCard(long id, string side, CancellationToken cancellationToken)
    {
        _ = await FindAsync(id, cancellationToken);
        var (front, back) = await _udhaar.IdCardPathsAsync(id, cancellationToken);

        var path = side.ToLowerInvariant() switch
        {
            "front" => front,
            "back" => back,
            _ => throw new BusinessRuleViolationException("The side is 'front' or 'back'."),
        };

        var opened = path is null ? null : _images.OpenPaymentProof(path);

        if (opened is null)
        {
            throw new NotFoundException("ID card photo", id);
        }

        Response.Headers.CacheControl = "private, no-store";

        return File(opened.Value.Content, opened.Value.ContentType);
    }

    private async Task<UdhaarCustomerRow> FindAsync(long id, CancellationToken cancellationToken) =>
        await _udhaar.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Customer", id);

    private async Task<string> SaveAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();

        return await _images.SaveIdCardAsync(stream, file.ContentType, file.Length, cancellationToken);
    }
}
