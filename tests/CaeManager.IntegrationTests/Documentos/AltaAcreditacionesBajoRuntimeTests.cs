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
/// Documento, que el índice único real no ve duplicados al repetir el alta, que
/// desde otro Tenant la regla no encuentra nada que acreditar, que la vía de
/// Empresa (regla A-1) acredita en Centros de plataforma sin Trabajadores, que el
/// choque real con el índice único del par se trata como «ya existe», y que la
/// conciliación solo crea en el Tenant en curso.
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

    [Fact]
    public async Task Un_documento_de_empresa_se_acredita_bajo_RLS_en_un_centro_de_plataforma_de_su_empresa_sin_trabajadores()
    {
        await using var arnes = await CrearArnesAsync();
        var escenario = await SembrarEmpresaSinTrabajadoresAsync(arnes);

        var documento = Documento.DeEmpresa(
            escenario.EmpresaId, escenario.TipoId, Emision, VigenciaDocumento.VenceEl(Emision.AddYears(5)));

        // Desde otro Tenant, los Centros de esa Empresa y sus accesos no son visibles: nada que acreditar.
        _tenant.TenantId = _otroTenant;
        (await AltaAsync(arnes, documento, guardarDocumento: false)).Should().Be(0);

        _tenant.TenantId = _tenantPropietario;
        (await AltaAsync(arnes, documento, guardarDocumento: true)).Should().Be(2,
            "los dos Centros de plataforma de la Empresa, sin nadie asignado; el de correo no cuenta");

        var acreditaciones = await LeerComoPropietarioAsync(arnes, documento.Id);
        acreditaciones.Select(a => a.CanalId).Should().BeEquivalentTo([escenario.CanalId, escenario.OtroCanalId]);
        acreditaciones.Should().OnlyContain(a => a.TenantId == _tenantPropietario);
    }

    [Fact]
    public async Task Un_par_que_otra_operacion_guarda_entre_la_lectura_y_el_guardado_se_trata_como_ya_existente()
    {
        await using var arnes = await CrearArnesAsync();
        var escenario = await SembrarEmpresaSinTrabajadoresAsync(arnes);
        _tenant.TenantId = _tenantPropietario;

        // Un Documento de Empresa ya guardado y todavía sin acreditar (como uno anterior a la regla A-1).
        var documento = Documento.DeEmpresa(
            escenario.EmpresaId, escenario.TipoId, Emision, VigenciaDocumento.VenceEl(Emision.AddYears(5)));
        await using (var scope = arnes.Servicios.CreateAsyncScope())
        {
            var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
        }

        // La operación B lee y agrega sus dos pares sin guardar todavía...
        await using var scopeB = arnes.Servicios.CreateAsyncScope();
        var contextoB = scopeB.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        (await AltaAcreditacionesDePrueba.Con(contextoB).AgregarPendientesAsync(new AltasConAcreditacion
        {
            CentrosAConciliar = [escenario.CentroId, escenario.OtroCentroId], SoloDocumentosDeEmpresa = true,
        })).Should().Be(2);

        // ...y la operación A confirma uno de ellos antes.
        await using (var scopeA = arnes.Servicios.CreateAsyncScope())
        {
            var contextoA = scopeA.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            contextoA.AcreditacionesDocumentoPlataforma.Add(new AcreditacionDocumentoPlataforma(documento.Id, escenario.CanalId));
            await contextoA.SaveChangesAsync();
        }

        // El choque con el índice único no hace fallar a B: el par ya existe y el resto se guarda.
        await contextoB.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();

        var acreditaciones = await LeerComoPropietarioAsync(arnes, documento.Id);
        acreditaciones.Select(a => a.CanalId).Should().BeEquivalentTo([escenario.CanalId, escenario.OtroCanalId],
            "un solo par por acceso: el de A y el que solo B agregaba");
    }

    [Fact]
    public async Task La_conciliacion_crea_bajo_RLS_lo_que_falta_solo_en_el_Tenant_en_curso_y_repetirla_no_duplica()
    {
        await using var arnes = await CrearArnesAsync();
        var escenario = await SembrarEmpresaSinTrabajadoresAsync(arnes);
        _tenant.TenantId = _tenantPropietario;

        var documento = Documento.DeEmpresa(
            escenario.EmpresaId, escenario.TipoId, Emision, VigenciaDocumento.VenceEl(Emision.AddYears(5)));
        await using (var scope = arnes.Servicios.CreateAsyncScope())
        {
            var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
        }

        _tenant.TenantId = _otroTenant;
        (await ConciliarAsync(arnes)).Should().Be(0, "desde otro Tenant no ve ni el Documento ni los Centros");
        (await LeerComoPropietarioAsync(arnes, documento.Id)).Should().BeEmpty();

        _tenant.TenantId = _tenantPropietario;
        (await ConciliarAsync(arnes)).Should().Be(2);
        (await ConciliarAsync(arnes)).Should().Be(0, "idempotente");

        var acreditaciones = await LeerComoPropietarioAsync(arnes, documento.Id);
        acreditaciones.Select(a => a.CanalId).Should().BeEquivalentTo([escenario.CanalId, escenario.OtroCanalId]);
        acreditaciones.Should().OnlyContain(a => a.TenantId == _tenantPropietario);
    }

    private static async Task<int> ConciliarAsync(ArnesDeArranqueRuntime arnes)
    {
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync())
            .Should().Be("cae_app_runtime");
        return await new ConciliacionAcreditacionesEmpresaService(contexto, AltaAcreditacionesDePrueba.Con(contexto), contexto)
            .ConciliarTenantEnCursoAsync();
    }

    private sealed record EscenarioEmpresa(
        Guid EmpresaId, Guid TipoId, Guid CentroId, Guid CanalId, Guid OtroCentroId, Guid OtroCanalId);

    /// <summary>
    /// En el Tenant propietario: una Empresa con dos Centros suyos con acceso de plataforma y uno por correo, sin ningún
    /// Trabajador asignado en ninguno, y un tipo de Empresa obligatorio por defecto.
    /// </summary>
    private async Task<EscenarioEmpresa> SembrarEmpresaSinTrabajadoresAsync(ArnesDeArranqueRuntime arnes)
    {
        _tenant.TenantId = _tenantPropietario;
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        // Los mismos CIF que SembrarAsync: cada test tiene su propio Tenant propietario.
        var cliente = Empresa.CrearComoCliente("Cliente Runtime A1 S.L.", "B10380194", false, null, null);
        var empresa = new Empresa("Empresa Runtime A1 S.L.", "B10380186");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Planta sin personal");
        var otroCentro = new Centro(cliente.Id, empresa.Id, "Otra planta sin personal");
        var centroPorCorreo = new Centro(cliente.Id, empresa.Id, "Planta por correo");
        contexto.Centros.AddRange(centro, otroCentro, centroPorCorreo);
        await contexto.SaveChangesAsync();

        var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
        var canal = CanalGestionDocumental.DePlataforma(
            centro.Id, "Plataforma", proveedor.Id, "https://plataforma.test", "usuario", "clave");
        var otroCanal = CanalGestionDocumental.DePlataforma(
            otroCentro.Id, "Plataforma", proveedor.Id, "https://plataforma.test", "usuario", "clave");
        contexto.CanalesGestionDocumental.AddRange(
            canal, otroCanal, CanalGestionDocumental.PorEmail(centroPorCorreo.Id, "Correo CAE", "cae@contratista.es", null));

        var tipo = new TipoDocumento("Seguro de responsabilidad civil", 12, aplicaVencimientoAutomatico: true, orden: 1,
            AmbitoAplicacion.Empresa, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        return new EscenarioEmpresa(empresa.Id, tipo.Id, centro.Id, canal.Id, otroCentro.Id, otroCanal.Id);
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
