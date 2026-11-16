using CaeManager.Infrastructure.Autenticacion;

namespace CaeManager.Web.Tests;

/// <summary>Marcas de una sesión de origen recién iniciada y con interacción ahora, para emitir tokens de extensión.</summary>
internal static class MarcasSesionDePrueba
{
    public static MarcasSesionOrigen Recientes()
    {
        var ahora = DateTimeOffset.UtcNow;
        return new MarcasSesionOrigen(ahora, ahora);
    }
}
