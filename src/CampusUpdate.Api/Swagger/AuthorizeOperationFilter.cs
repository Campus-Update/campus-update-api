using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CampusUpdate.Api.Swagger;

public sealed class AuthorizeOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var authorization = metadata.OfType<IAuthorizeData>().ToArray();
        if (metadata.OfType<IAllowAnonymous>().Any() || authorization.Length == 0)
            return;

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
        });

        var roles = authorization
            .SelectMany(item => (item.Roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var accessDescription = roles.Length == 0
            ? "Authentication is required."
            : $"Required role: {string.Join(", ", roles)}.";
        operation.Description = string.IsNullOrWhiteSpace(operation.Description)
            ? accessDescription
            : $"{operation.Description.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{accessDescription}";
    }
}
