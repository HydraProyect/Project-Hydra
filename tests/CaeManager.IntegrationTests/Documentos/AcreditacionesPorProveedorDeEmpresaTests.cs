using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// La pestaña «Plataformas» de Empresa 360 reutiliza <see cref="ObtenerAcreditacionesPorProveedorQuery"/> acotada a una
/// Empresa (<c>SoloDocumentosDeEmpresaId</c>): solo los documentos de ESA Empresa —no los de sus Trabajadores ni los de
/// otra Empresa— y solo si la Empresa es visible para quien pregunta.
/// </summary>
public class AcreditacionesPorProveedorDeEmpresaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private async Task<(Guid EmpresaId, Guid OtraEmpresaId, Guid DocumentoDeLaEmpresaId)> SembrarAsync()
    {
        await using var contexto = CrearContexto();
        var cliente = Empresa.CrearComoCliente("Cliente Plataformas S.L.", "B10380194", false, null, null);
        var empresa = new Empresa("Empresa Plataformas S.L.", "B10380186");
        var otra = new Empresa("Otra Plataformas S.L.", "B10380178");
        contexto.Empresas.AddRange(cliente, empresa, otra);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Planta Plataformas");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Gestión general", proveedor.Id, null, null, null);
        contexto.CanalesGestionDocumental.Add(canal);

        var tipoEmpresa = new TipoDocumento("Seguro de responsabilidad civil", 12, true, 1, AmbitoAplicacion.Empresa);
        var tipoTrabajador = new TipoDocumento("Apto médico", 12, true, 2, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.AddRange(tipoEmpresa, tipoTrabajador);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Nora", "Vidal", "22334455Y");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        var hoy = CaeManager.Domain.Common.DiaDeNegocio.Hoy();
        var documentoEmpresa = Documento.DeEmpresa(empresa.Id, tipoEmpresa.Id, hoy.AddMonths(-2), VigenciaDocumento.VenceEl(hoy.AddMonths(10)));
        var documentoOtra = Documento.DeEmpresa(otra.Id, tipoEmpresa.Id, hoy.AddMonths(-2), VigenciaDocumento.VenceEl(hoy.AddMonths(10)));
        var documentoTrabajador = Documento.DeTrabajador(trabajador.Id, tipoTrabajador.Id, hoy.AddMonths(-2), VigenciaDocumento.VenceEl(hoy.AddMonths(10)));
        contexto.Documentos.AddRange(documentoEmpresa, documentoOtra, documentoTrabajador);
        await contexto.SaveChangesAsync();

        var aceptada = new AcreditacionDocumentoPlataforma(documentoEmpresa.Id, canal.Id);
        aceptada.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        contexto.AcreditacionesDocumentoPlataforma.AddRange(
            aceptada,
            new AcreditacionDocumentoPlataforma(documentoOtra.Id, canal.Id),
            new AcreditacionDocumentoPlataforma(documentoTrabajador.Id, canal.Id));
        await contexto.SaveChangesAsync();

        return (empresa.Id, otra.Id, documentoEmpresa.Id);
    }

    private async Task<IReadOnlyList<ProveedorAcreditacionesDto>> ConsultarAsync(ObtenerAcreditacionesPorProveedorQuery consulta, AlcanceDatosServiceFalso? alcance = null)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerAcreditacionesPorProveedorQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, alcance ?? new AlcanceDatosServiceFalso());
        return await handler.Handle(consulta, CancellationToken.None);
    }

    [Fact]
    public async Task Acotada_a_la_Empresa_solo_trae_sus_propios_documentos_y_no_los_de_sus_Trabajadores_ni_los_de_otra_Empresa()
    {
        var (empresaId, _, documentoId) = await SembrarAsync();

        var sinAcotar = await ConsultarAsync(new ObtenerAcreditacionesPorProveedorQuery(IncluirAceptadas: true));
        sinAcotar.SelectMany(p => p.Clientes).SelectMany(c => c.Documentos).Should().HaveCount(3, "el escenario tiene tres acreditaciones de otra naturaleza");

        var acotada = await ConsultarAsync(new ObtenerAcreditacionesPorProveedorQuery(IncluirAceptadas: true, SoloDocumentosDeEmpresaId: empresaId));

        var fila = acotada.SelectMany(p => p.Clientes).SelectMany(c => c.Documentos).Should().ContainSingle().Subject;
        fila.DocumentoId.Should().Be(documentoId);
        fila.EmpresaId.Should().Be(empresaId);
        fila.TrabajadorId.Should().BeNull();
        fila.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        fila.CentroNombre.Should().Be("Planta Plataformas");
    }

    [Fact]
    public async Task Acotada_a_una_Empresa_fuera_de_alcance_devuelve_la_lista_vacia()
    {
        var (empresaId, otraId, _) = await SembrarAsync();

        var resultado = await ConsultarAsync(
            new ObtenerAcreditacionesPorProveedorQuery(IncluirAceptadas: true, SoloDocumentosDeEmpresaId: empresaId),
            new AlcanceDatosServiceFalso(empresaIds: [otraId]));

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_alcance_de_gestion_la_lista_sale_vacia_aunque_la_Empresa_sea_visible()
    {
        // Un usuario de portal ve la Empresa pero no gestiona ningún Centro: el filtro por Empresa no le abre las plataformas.
        var (empresaId, _, _) = await SembrarAsync();

        var resultado = await ConsultarAsync(
            new ObtenerAcreditacionesPorProveedorQuery(IncluirAceptadas: true, SoloDocumentosDeEmpresaId: empresaId),
            new AlcanceDatosServiceFalso(centroIdsParaGestion: []));

        resultado.Should().BeEmpty();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
