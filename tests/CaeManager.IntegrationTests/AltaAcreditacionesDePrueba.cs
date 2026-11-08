using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Gestiones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;

namespace CaeManager.IntegrationTests;

/// <summary>
/// El servicio real de <see cref="IAltaAcreditacionesPlataformaService"/>
/// sobre un contexto de prueba, para los tests que construyen los handlers a
/// mano. Es el mismo servicio que registra la inyección de dependencias: los
/// tests de integración observan la regla de verdad, no un doble.
/// </summary>
public static class AltaAcreditacionesDePrueba
{
    public static AltaAcreditacionesPlataformaService Con(CaeManagerDbContext contexto) =>
        new(contexto, contexto, contexto, contexto, contexto, new AcreditacionDocumentoPlataformaRepository(contexto));

    /// <summary>
    /// El servicio real de <see cref="INacimientoGestionAutomaticaService"/> sobre el mismo contexto: la Gestión
    /// automática nace en el mismo guardado que el Documento y sus acreditaciones, como en producción.
    /// </summary>
    public static NacimientoGestionAutomaticaService GestionAutomatica(
        CaeManagerDbContext contexto, ICurrentUserService? usuario = null) =>
        new(new GestionAutomaticaRepository(contexto), new AcreditacionDocumentoPlataformaRepository(contexto),
            contexto, contexto, usuario ?? new CurrentUserServiceFalso(), TimeProvider.System);
}
