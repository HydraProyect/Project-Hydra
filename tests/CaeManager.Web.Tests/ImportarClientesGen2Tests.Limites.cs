using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Importacion.Queries;
using CaeManager.Infrastructure.Importacion;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaginaImportacion = CaeManager.Web.Features.Importacion.Pages.Importacion;

namespace CaeManager.Web.Tests;

public partial class ImportarClientesGen2Tests
{
    [Theory]
    [InlineData("filas", "máximo de filas", LogLevel.Warning)]
    [InlineData("celdas", "máximo de celdas", LogLevel.Warning)]
    [InlineData("inspector", "No pudimos leer este archivo", LogLevel.Warning)]
    [InlineData("otra", "No pudimos leer este archivo", LogLevel.Error)]
    public async Task Rechazo_del_inspector_se_registra_como_warning_sin_pila_y_lo_demas_como_error(string caso, string textoEsperado, LogLevel nivel)
    {
        var registro = new RegistroImportacion();
        var escenario = new Escenario
        {
            Interceptar = peticion => peticion is AnalizarPlantillaClientesQuery
                ? Task.FromException<object?>(caso switch
                {
                    "filas" => new LimiteHojaImportacionException(true),
                    "celdas" => new LimiteHojaImportacionException(false),
                    "inspector" => new ArchivoRechazadoException("dato privado"),
                    _ => new InvalidDataException("fallo real del analisis")
                })
                : null
        };
        Services.AddSingleton<ILogger<PaginaImportacion>>(registro);
        var (cut, _) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, "privado.xlsx", "datos");
        registro.Eventos.Should().ContainSingle();
        registro.Eventos[0].nivel.Should().Be(nivel);
        registro.Eventos[0].mensaje.Should().NotContain("privado");
        if (nivel == LogLevel.Warning) registro.Eventos[0].excepcion.Should().BeNull();
        else registro.Eventos[0].excepcion.Should().NotBeNull();
        cut.Find(".alerta-formulario").TextContent.Should().Contain(textoEsperado);
    }

    [Fact]
    public async Task Sin_hueco_para_analizar_se_rechaza_con_el_aviso_y_sin_llamar_al_analisis()
    {
        Services.Configure<OpcionesLimitesImportacion>(o => { o.AnalisisConcurrentes = 1; o.EsperaSegundos = 0; });
        var analizado = false;
        var escenario = new Escenario
        {
            Interceptar = peticion =>
            {
                analizado |= peticion is AnalizarPlantillaClientesQuery;
                return null;
            }
        };
        var (cut, _) = Renderizar(escenario);
        using var ocupado = await cut.Services.GetRequiredService<LimitadorAnalisisImportacion>().IntentarEntrarAsync(TenantImportacion);
        ocupado.Should().NotBeNull();
        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, "datos.xlsx", "datos");

        cut.Find(".alerta-formulario").TextContent.Should().Contain("demasiados análisis");
        analizado.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_tenant_resuelto_se_rechaza_sin_reservar_hueco_y_sin_llamar_al_analisis()
    {
        Services.Configure<OpcionesLimitesImportacion>(o => { o.AnalisisConcurrentes = 1; o.EsperaSegundos = 0; });
        var analizado = false;
        var escenario = new Escenario
        {
            Interceptar = peticion =>
            {
                analizado |= peticion is AnalizarPlantillaClientesQuery;
                return null;
            }
        };
        var (cut, _) = Renderizar(escenario);
        ((TenantEfectivoDePrueba)cut.Services.GetRequiredService<ITenantActual>()).TenantId = null;
        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, "datos.xlsx", "datos");

        cut.Find(".alerta-formulario").TextContent.Should().Contain("con qué organización trabajas");
        analizado.Should().BeFalse();
        using var libre = await cut.Services.GetRequiredService<LimitadorAnalisisImportacion>().IntentarEntrarAsync(Guid.NewGuid());
        libre.Should().NotBeNull("rechazar por falta de Tenant no debe ocupar el único hueco global");
    }

    [Fact]
    public async Task El_hueco_se_libera_aunque_el_analisis_falle()
    {
        Services.Configure<OpcionesLimitesImportacion>(o => { o.AnalisisConcurrentes = 1; o.EsperaSegundos = 0; });
        var escenario = new Escenario
        {
            Interceptar = peticion => peticion is AnalizarPlantillaClientesQuery
                ? Task.FromException<object?>(new ArchivoRechazadoException("x"))
                : null
        };
        var (cut, _) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, "datos.xlsx", "datos");

        using var libre = await cut.Services.GetRequiredService<LimitadorAnalisisImportacion>().IntentarEntrarAsync(TenantImportacion);
        libre.Should().NotBeNull("el permiso del análisis fallido debe haberse devuelto");
    }

    private sealed class RegistroImportacion : ILogger<PaginaImportacion>
    {
        public List<(LogLevel nivel, Exception? excepcion, string mensaje)> Eventos { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Eventos.Add((logLevel, exception, formatter(state, exception)));
    }
}
