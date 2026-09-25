using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// La regla única de alta de acreditaciones de plataforma (P0-7,
/// <see cref="AltaAcreditacionesPlataformaService"/>) con el cableado de
/// producción: conexión como <c>cae_app_runtime</c> y los cuatro interceptores
/// (<see cref="ArnesDeArranqueRuntime"/>). Lo que mide y los tests de
/// Application no pueden: que PostgreSQL traduce y ejecuta sus consultas bajo
/// RLS, que la acreditación queda sellada con el Tenant propietario del
/// Documento, que el índice único real no ve duplicados al repetir el alta, y
/// que desde otro Tenant la regla no encuentra nada que acreditar.
/// </summary>
public class AltaAcreditacionesBajoRuntimeTests
{
    private static readonly DateOnly Emision = new(2026, 1, 15);

    private readonly Guid _tenantPropietario = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();
    private readonly TenantActualAmbiental _tenant = new();

    [Fact]
    public async Task Acredita_solo_ante_el_Centro_que_exige_el_tipo_sellada_con_el_Tenant_propietario_y_sin_duplicar()
    {
        await using var arnes = await CrearArnesAsync();
        var escenario = await SembrarAsync(arnes);

        var documento = Documento.DeTrabajador(
            escenario.TrabajadorId, escenario.TipoId, Emision, VigenciaDocumento.VenceEl(Emision.AddYears(5)));

        _tenant.TenantId = _tenantPropietario;
        (await AltaAsync(arnes, documento, guardarDocumento: true)).Should().Be(1,
            "un acceso de plataforma en el Centro que exige el tipo; el Centro que lo excluye no cuenta");

        var acreditaciones = await LeerComoPropietarioAsync(arnes, documento.Id);
        acreditaciones.Should().ContainSingle();
        acreditaciones[0].CanalId.Should().Be(escenario.CanalCentroQueExigeId);
        acreditaciones[0].TenantId.Should().Be(_tenantPropietario,
            "la acreditación es del Tenant propietario del Documento");

        (await AltaAsync(arnes, documento, guardarDocumento: false)).Should().Be(0,
            "repetir el alta no agrega una acreditación que ya existe");
        (await LeerComoPropietarioAsync(arnes, documento.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Desde_otro_Tenant_la_regla_no_encuentra_actividad_que_acreditar()
    {
        await using var arnes = await CrearArnesAsync();
        var escenario = await SembrarAsync(arnes);

        // Un Documento que nombra al Trabajador del Tenant propietario, dado de
        // alta con el contexto de otro Tenant: sus Asignaciones, Centros y
        // accesos no son visibles desde ahí.
        var documento = Documento.DeTrabajador(
            escenario.TrabajadorId, escenario.TipoId, Emision, VigenciaDocumento.VenceEl(Emision.AddYears(5)));

        _tenant.TenantId = _otroTenant;
        (await AltaAsync(arnes, documento, guardarDocumento: false)).Should().Be(0);

        _tenant.TenantId = _tenantPropietario;
        (await AltaAsync(arnes, documento, guardarDocumento: true)).Should().Be(1,
            "control positivo: el mismo Documento en su Tenant propietario sí se acredita");
    }

    private sealed record Escenario(Guid TrabajadorId, Guid TipoId, Guid CanalCentroQueExigeId);

    private Task<ArnesDeArranqueRuntime> CrearArnesAsync() =>
        ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false, tenantActualPersonalizado: _tenant);

    /// <summary>
    /// En el Tenant propietario: un Trabajador asignado a dos Centros, cada uno
    /// con su acceso de plataforma. El tipo es obligatorio por defecto; un
    /// Centro lo exige (sin fila) y el otro lo excluye (fila con Incluido = false).
    /// </summary>
    private async Task<Escenario> SembrarAsync(ArnesDeArranqueRuntime arnes)
    {
        _tenant.TenantId = _tenantPropietario;
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var cliente = Empresa.CrearComoCliente("Cliente Runtime S.L.", "B10380194", false, null, null);
        var empresa = new Empresa("Empresa Runtime S.L.", "B10380186");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centroQueExige = new Centro(cliente.Id, empresa.Id, "Centro que exige");
        var centroQueExcluye = new Centro(cliente.Id, empresa.Id, "Centro que excluye");
        contexto.Centros.AddRange(centroQueExige, centroQueExcluye);
        await contexto.SaveChangesAsync();

        var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
        var canalQueExige = CanalGestionDocumental.DePlataforma(
            centroQueExige.Id, "Plataforma", proveedor.Id, "https://plataforma.test", "usuario", "clave");
        var canalQueExcluye = CanalGestionDocumental.DePlataforma(
            centroQueExcluye.Id, "Plataforma", proveedor.Id, "https://plataforma.test", "usuario", "clave");
        contexto.CanalesGestionDocumental.AddRange(canalQueExige, canalQueExcluye);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Gómez", "12345678Z");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        var tipo = new TipoDocumento("Formación PRL", 12, aplicaVencimientoAutomatico: true, orden: 1,
            AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.AddRange(
            new Asignacion(trabajador.Id, centroQueExige.Id, Emision),
            new Asignacion(trabajador.Id, centroQueExcluye.Id, Emision));
        contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipo.Id, centroQueExcluye.Id, incluido: false));
        await contexto.SaveChangesAsync();

        return new Escenario(trabajador.Id, tipo.Id, canalQueExige.Id);
    }

    /// <summary>
    /// Como un Command: agrega el Documento (si toca), pide a la regla sus
    /// acreditaciones y confirma las dos cosas en el mismo guardado.
    /// </summary>
    private static async Task<int> AltaAsync(ArnesDeArranqueRuntime arnes, Documento documento, bool guardarDocumento)
    {
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync())
            .Should().Be("cae_app_runtime");

        if (guardarDocumento)
            contexto.Documentos.Add(documento);

        var agregadas = await AltaAcreditacionesDePrueba.Con(contexto)
            .AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });
        await contexto.SaveChangesAsync();
        return agregadas;
    }

    private static async Task<List<(Guid CanalId, Guid TenantId)>> LeerComoPropietarioAsync(
        ArnesDeArranqueRuntime arnes, Guid documentoId)
    {
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = """
            SELECT "CanalGestionDocumentalId", "TenantId" FROM "AcreditacionesDocumentoPlataforma"
            WHERE "DocumentoId" = @documento;
            """;
        consulta.Parameters.AddWithValue("documento", documentoId);

        var filas = new List<(Guid, Guid)>();
        await using var lector = await consulta.ExecuteReaderAsync();
        while (await lector.ReadAsync()) filas.Add((lector.GetGuid(0), lector.GetGuid(1)));
        return filas;
    }
}
