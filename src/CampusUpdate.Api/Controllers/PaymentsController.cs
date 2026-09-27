using CampusUpdate.Api.Contracts;
using CampusUpdate.Api.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/payments")]
public sealed class PaymentsController(PaystackClient paystack) : ControllerBase
{
    [HttpPost("initialize")]
    public async Task<ActionResult<PaymentResponse>> Initialize(InitializePaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var transaction = await paystack.InitializeAsync(request.Email.Trim(), request.Amount, request.Reference.Trim(), request.CallbackUrl, cancellationToken);
            return Ok(new PaymentResponse(transaction.AuthorizationUrl, transaction.AccessCode, transaction.Reference));
        }
        catch (PaystackNotConfiguredException ex) { return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = ex.Message }); }
        catch (PaystackException ex) { return BadRequest(new ProblemDetails { Title = ex.Message }); }
    }

    [HttpGet("verify/{reference}")]
    public async Task<ActionResult<PaymentVerificationResponse>> Verify(string reference, CancellationToken cancellationToken)
    {
        try
        {
            var transaction = await paystack.VerifyAsync(reference.Trim(), cancellationToken);
            return Ok(new PaymentVerificationResponse(transaction.Reference, transaction.Status, transaction.Amount, transaction.Currency, transaction.PaidAt));
        }
        catch (PaystackNotConfiguredException ex) { return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = ex.Message }); }
        catch (PaystackException ex) { return BadRequest(new ProblemDetails { Title = ex.Message }); }
    }
}
