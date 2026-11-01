namespace CaeManager.Web.Api.V1;

/// <summary>El alcance que exige una ruta de <c>/api/v1</c> a la Clave de Integración del Tenant.</summary>
public sealed record AlcanceIntegracionRequerido(string Alcance);

/// <summary>
/// Alcances por ruta (ASVS V13.1.4, OWASP API5:2023), con denegación por defecto: una ruta sin alcance
/// declarado no existe para una Clave de Integración del Tenant. Lo vigila
/// <c>RutasApiV1DeclaranAlcanceTests</c>. La Clave de Soporte TALVEG conserva lo que ya leía.
/// </summary>
public sealed class AlcanceIntegracionEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var usuario = context.HttpContext.User;
        if (ApiV1.EsClaveDeSoporte(usuario))
            return await next(context);

        var requerido = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AlcanceIntegracionRequerido>();

        // Nunca Results.Forbid(): resolvería el esquema por defecto, la cookie, y redirigiría al inicio de sesión.
        return requerido is not null
            ? await next(context)
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden);
    }
}

public static class AlcanceIntegracionExtensions
{
    public static RouteHandlerBuilder RequiereAlcanceIntegracion(this RouteHandlerBuilder ruta, string alcance) =>
        ruta.WithMetadata(new AlcanceIntegracionRequerido(alcance));
}
