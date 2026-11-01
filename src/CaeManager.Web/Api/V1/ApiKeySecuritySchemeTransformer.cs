using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CaeManager.Web.Api.V1;

/// <summary>Documenta el esquema de autenticación (header <c>Authorization: ApiKey {clave}</c>) en el OpenAPI de /api/v1.</summary>
public class ApiKeySecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "TALVEG API v1 (no publicada)";
        document.Info.Description = "Superficie de solo lectura para integraciones autorizadas. Sin anunciar todavía — ver docs/business/MATURITY_REVIEW.md. Autenticación: \"Authorization: Bearer {token}\" con una Clave de Integración del Tenant, o \"Authorization: ApiKey {clave}\" con una Clave de Soporte TALVEG.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description = "Formato: ApiKey {clave}",
        };
        document.Components.SecuritySchemes["ClaveIntegracion"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = new OpenApiOAuthFlows
            {
                ClientCredentials = new OpenApiOAuthFlow { TokenUrl = new Uri("/api/v1/token", UriKind.Relative) },
            },
        };

        return Task.CompletedTask;
    }
}
