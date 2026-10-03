using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// Lo de <see cref="EscenarioDeFotoDeSuperficies"/> que depende del bloqueo por Centro y de la tolerancia: cómo se siembra
/// la tolerancia, cómo se quita (para fotografiar «tolerancia 0 en todo») y las dos secciones de la foto que la usan (Mi
/// trabajo y Trabajadores bloqueados por Centro).
/// </summary>
internal static class FotoDeAcceso
{
    internal const int ToleranciaDelClienteX = 15;
    internal const int ToleranciaDelClienteDos = 100;

    /// <summary>
    /// Tenant uno: el Cliente empresarial X da 15 días por defecto para el PSS y el certificado; el Centro B las personaliza
    /// a 0; el Cliente Y no fija ninguna (0). Tenant dos: su Cliente empresarial da 100 días para el PSS.
    /// </summary>
    public static async Task SembrarTolerancias(EscenarioDeFotoDeSuperficies escenario)
    {
        await using (var c = escenario.CrearContexto(escenario.TenantUno))
        {
            var clienteX = await c.Empresas.SingleAsync(e => e.RazonSocial == "Cliente X");
            foreach (var tipo in await c.TiposDocumento.ToListAsync())
                c.ToleranciasDocumentoClienteEmpresarial.Add(new ToleranciaDocumentoClienteEmpresarial(clienteX.Id, tipo.Id, ToleranciaDelClienteX));

            var centroB = await c.Centros.SingleAsync(x => x.Nombre == "Centro B");
            foreach (var fila in await c.TiposDocumentoCentros.Where(f => f.CentroId == centroB.Id).ToListAsync())
                Fijar(fila, 0);
            await c.SaveChangesAsync();
        }

        await using (var c = escenario.CrearContexto(escenario.TenantDos))
        {
            var cliente = await c.Empresas.SingleAsync(e => e.RazonSocial == "Cliente X2");
            var pss = await c.TiposDocumento.SingleAsync();
            c.ToleranciasDocumentoClienteEmpresarial.Add(new ToleranciaDocumentoClienteEmpresarial(cliente.Id, pss.Id, ToleranciaDelClienteDos));
            await c.SaveChangesAsync();
        }
    }

    private static void Fijar(TipoDocumentoCentro fila, int? tolerancia) =>
        fila.Actualizar(fila.Incluido, fila.PeriodicidadEspecialMeses, fila.BloqueaAcceso, fila.ArchivoUrl, fila.NombreArchivoOriginal, tolerancia);

    /// <summary>Deja los mismos datos con tolerancia 0 en todo: sin filas de Cliente empresarial y sin personalizaciones de Centro.</summary>
    public static async Task QuitarTolerancias(EscenarioDeFotoDeSuperficies escenario)
    {
        foreach (var tenant in new[] { escenario.TenantUno, escenario.TenantDos })
        {
            await using var c = escenario.CrearContexto(tenant);
            c.ToleranciasDocumentoClienteEmpresarial.RemoveRange(await c.ToleranciasDocumentoClienteEmpresarial.ToListAsync());
            foreach (var fila in await c.TiposDocumentoCentros.Where(f => f.ToleranciaDias != null).ToListAsync())
                Fijar(fila, null);
            await c.SaveChangesAsync();
        }
    }

    public static async Task<IEnumerable<KeyValuePair<string, List<string>>>> TomarAsync(
        CaeManager.Infrastructure.Persistence.CaeManagerDbContext c, IAlcanceDatosService alcance,
        Func<Guid, string> centro, Func<Guid, string> trabajador, Func<Guid, string> empresa, Func<Guid, string> tipo)
    {
        var evaluacionDeAcceso = new EvaluacionDeAccesoPorCentroService(c, c, c, c, c, alcance);
        var filas = await new ObtenerDocumentacionBloqueantePendienteQueryHandler(c, c, c, evaluacionDeAcceso)
            .Handle(new ObtenerDocumentacionBloqueantePendienteQuery(), CancellationToken.None);

        var miTrabajo = filas
            .Select(f => $"{centro(f.CentroId)} | {trabajador(f.TrabajadorId)} | {tipo(f.TipoDocumentoId)} | {f.Ambito} | {f.Situacion}")
            .ToList();

        // Trabajadores bloqueados por Centro: quién está bloqueado en cada Centro (un Trabajador, un Centro).
        var bloqueados = filas
            .GroupBy(f => (f.CentroId, f.TrabajadorId))
            .Select(g => $"{centro(g.Key.CentroId)} | {trabajador(g.Key.TrabajadorId)}")
            .ToList();

        return
        [
            new("Mi trabajo", miTrabajo),
            new("Trabajadores bloqueados por Centro", bloqueados)
        ];
    }
}
