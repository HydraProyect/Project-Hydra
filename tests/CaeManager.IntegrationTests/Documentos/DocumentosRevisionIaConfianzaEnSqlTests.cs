using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// La columna «Revisión IA» del listado de Documentos con el filtro «Pendiente de revisión IA»: la consulta de confianza
/// (<c>GroupBy</c> + <c>Min</c> sobre <c>ColaRevisionIa.Pendientes</c>) se traduce a SQL, y solo PostgreSQL lo comprueba.
/// Un Documento con dos revisiones pendientes y una resuelta enseña la menor de las pendientes; con un rol que no accede a la
/// cola (Cliente) el filtro no se aplica y no viaja ninguna confianza.
/// </summary>
public class DocumentosRevisionIaConfianzaEnSqlTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _conVariasId;
    private Guid _conUnaId;
    private Guid _sinRevisionId;
    private Guid _soloResueltaId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var parametros = await contexto.ParametrosSistema.SingleOrDefaultAsync();
        if (parametros is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));
        else
            parametros.Actualizar(30, 15);

        var cliente = Empresa.CrearComoCliente("Visible S.L.", "B12345674", false, null, null);
        var tipo = new TipoDocumento("Seguro RC", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.Empresas.Add(cliente);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var vigencia = VigenciaDocumento.VenceEl(_hoy.AddDays(200));
        var conVarias = Documento.DeCliente(cliente.Id, tipo.Id, _hoy.AddDays(-10), vigencia);
        var conUna = Documento.DeCliente(cliente.Id, tipo.Id, _hoy.AddDays(-20), vigencia);
        var sinRevision = Documento.DeCliente(cliente.Id, tipo.Id, _hoy.AddDays(-30), vigencia);
        var soloResuelta = Documento.DeCliente(cliente.Id, tipo.Id, _hoy.AddDays(-40), vigencia);
        contexto.Documentos.AddRange(conVarias, conUna, sinRevision, soloResuelta);
        await contexto.SaveChangesAsync();
        (_conVariasId, _conUnaId, _sinRevisionId, _soloResueltaId) = (conVarias.Id, conUna.Id, sinRevision.Id, soloResuelta.Id);

        // El Documento con varias: dos pendientes (80 y 55, sembradas en ese orden) y una resuelta con una confianza aún menor (20),
        // que no cuenta: la menor de las PENDIENTES es 55.
        var resuelta = RevisionIaDocumento.Crear(conVarias.Id, 20, null, null, null, null, "Revisada");
        resuelta.Resolver();
        contexto.RevisionesIaDocumento.AddRange(
            RevisionIaDocumento.Crear(conVarias.Id, 80, null, null, null, null, "Revisar"),
            RevisionIaDocumento.Crear(conVarias.Id, 55, null, null, null, null, "Revisar"),
            resuelta,
            RevisionIaDocumento.Crear(conUna.Id, 91, null, null, null, null, "Revisar"));
        var soloResueltaRevision = RevisionIaDocumento.Crear(soloResuelta.Id, 40, null, null, null, null, "Revisada");
        soloResueltaRevision.Resolver();
        contexto.RevisionesIaDocumento.Add(soloResueltaRevision);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Con_un_rol_de_gestion_documental_la_confianza_es_la_menor_de_las_revisiones_pendientes_del_Documento()
    {
        var resultado = await ConsultarAsync("GestorCae", pendienteDeRevisionIa: true);

        resultado.Elementos.Select(d => d.Id).Should().BeEquivalentTo([_conVariasId, _conUnaId],
            "el filtro deja solo los Documentos con alguna revisión pendiente: ni el que no tiene ninguna ni el que solo tiene una resuelta");
        resultado.Elementos.Single(d => d.Id == _conVariasId).ConfianzaRevisionIa.Should().Be(55,
            "80 y 55 pendientes: la menor; la resuelta de 20 no cuenta");
        resultado.Elementos.Single(d => d.Id == _conUnaId).ConfianzaRevisionIa.Should().Be(91);
    }

    [Fact]
    public async Task Con_el_rol_Cliente_el_filtro_no_se_aplica_y_no_viaja_ninguna_confianza()
    {
        var resultado = await ConsultarAsync("Cliente", pendienteDeRevisionIa: true);

        // Control positivo de que hay datos que filtrar: con un rol de gestión sí se estrecha (prueba de arriba); aquí salen los cuatro.
        resultado.Elementos.Select(d => d.Id).Should().BeEquivalentTo([_conVariasId, _conUnaId, _sinRevisionId, _soloResueltaId],
            "con un rol que no accede a la cola el filtro se ignora: ni estrecha ni vacía la lista");
        resultado.Elementos.Should().OnlyContain(d => d.ConfianzaRevisionIa == null, "sin la cola no se consulta ni viaja la confianza");
    }

    [Fact]
    public async Task Sin_el_filtro_ningun_rol_recibe_confianza()
    {
        var resultado = await ConsultarAsync("GestorCae", pendienteDeRevisionIa: false);

        resultado.Elementos.Should().HaveCount(4);
        resultado.Elementos.Should().OnlyContain(d => d.ConfianzaRevisionIa == null);
    }

    private async Task<ResultadoPaginado<DocumentoListaDto>> ConsultarAsync(string rol, bool pendienteDeRevisionIa)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentosQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso(), contexto, contexto,
            new UsuarioActualFalso(rol));
        return await handler.Handle(
            new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50, PendienteDeRevisionIa: pendienteDeRevisionIa),
            CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class UsuarioActualFalso(string rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
