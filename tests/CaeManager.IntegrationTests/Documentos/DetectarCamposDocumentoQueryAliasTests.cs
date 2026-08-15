using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Pieza 6 del backlog de gestión EPI: la identidad siempre se confirma por
/// DNI (ver DetectarCamposDocumentoQuery), pero si el nombre que trae el
/// documento no coincide ni con el Nombre+Apellidos oficiales ni con el
/// Alias ya guardado, se sugiere darlo de alta como Alias — un clic del
/// Gestor en vez de tener que ir a Trabajadores a mano. Contra Postgres real
/// porque DetectarTrabajadorAsync usa ToListAsync sobre el DbContext.
/// </summary>
public class DetectarCamposDocumentoQueryAliasTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Sugiere_alias_cuando_el_nombre_detectado_no_coincide_con_el_oficial_ni_con_el_alias()
    {
        var trabajadorId = await CrearTrabajadorAsync("Francisco", "Villa", alias: null);
        var handler = CrearHandler();

        var resultado = await handler.Handle(
            new DetectarCamposDocumentoQuery([1, 2, 3], "documento.pdf", AmbitoAplicacion.Trabajador), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TrabajadorId.Should().Be(trabajadorId);
        resultado.Valor.AliasSugerido.Should().Be("Pancho Villa");
    }

    [Fact]
    public async Task No_sugiere_alias_cuando_el_nombre_detectado_coincide_con_el_nombre_oficial()
    {
        await CrearTrabajadorAsync("Pancho", "Villa", alias: null);
        var handler = CrearHandler();

        var resultado = await handler.Handle(
            new DetectarCamposDocumentoQuery([1, 2, 3], "documento.pdf", AmbitoAplicacion.Trabajador), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.AliasSugerido.Should().BeNull();
    }

    [Fact]
    public async Task No_sugiere_alias_cuando_el_nombre_detectado_coincide_con_el_alias_ya_guardado()
    {
        await CrearTrabajadorAsync("Francisco", "Villa", alias: "Pancho Villa");
        var handler = CrearHandler();

        var resultado = await handler.Handle(
            new DetectarCamposDocumentoQuery([1, 2, 3], "documento.pdf", AmbitoAplicacion.Trabajador), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.AliasSugerido.Should().BeNull();
    }

    private async Task<Guid> CrearTrabajadorAsync(string nombre, string apellidos, string? alias)
    {
        await using var contexto = CrearContexto();
        var empresa = new CaeManager.Domain.Empresas.Empresa("Empresa Pancho Villa S.L.", "B87654323");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeEmpresa(empresa.Id, nombre, apellidos, "77189989B", alias: alias);
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        return trabajador.Id;
    }

    private DetectarCamposDocumentoQueryHandler CrearHandler()
    {
        var contexto = CrearContexto();

        var campos = new Dictionary<string, string?>
        {
            ["documentoIdentidadTrabajador"] = "77189989B",
            ["nombreTrabajador"] = "Pancho Villa"
        };
        var extraccion = new ExtraccionEstructuradaDto(TipoDetectado: null, campos, ConfianzaGeneral: 90, NotasValidacion: null);
        var router = new RouterFalso(Result.Exito(extraccion));

        return new DetectarCamposDocumentoQueryHandler(
            router, contexto, contexto, Options.Create(new DeteccionPreviaDocumentoOptions { Activa = true }));
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

    private sealed class RouterFalso(Result<ExtraccionEstructuradaDto> resultado) : IDocumentAIRouterService
    {
        public Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
            byte[] contenido, string nombreArchivo, string tipoEsperado, Guid? documentoId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(resultado);
    }
}
