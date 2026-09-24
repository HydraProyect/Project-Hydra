using CaeManager.Application.Documentos.Acreditacion;
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
}
