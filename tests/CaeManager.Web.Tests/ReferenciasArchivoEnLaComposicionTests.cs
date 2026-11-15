using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.FileStorage;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// ADR-013 M2 (K-17): la reparación de adjuntos temporales recogía la evidencia Inbound por nombre; desde M2 la recoge
/// porque Inbound la aporta por el puerto de contribución. Si la composición real dejara de registrarla, el barrido dejaría
/// fuera esas claves sin fallar. Program.cs solo puede construir la reparación con <see cref="ReparacionAdjuntosTemporales.Crear"/>
/// (el constructor es interno), así que esto fija la misma vía que usa el modo de mantenimiento.
/// </summary>
public class ReferenciasArchivoEnLaComposicionTests
{
    [Fact]
    public void La_reparacion_creada_desde_la_composicion_real_recoge_la_clave_de_la_evidencia_Inbound()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());
        using var proveedor = servicios.BuildServiceProvider();
        using var scope = proveedor.CreateScope();
        using var db = new DbContext(new DbContextOptionsBuilder().Options);

        var reparacion = ReparacionAdjuntosTemporales.Crear(scope.ServiceProvider, db);

        reparacion.CamposAportados.Select(c => (c.Entidad, c.Campo))
            .Should().Contain((typeof(EvidenciaInbound), "ArchivoUrl"));
    }

    /// <summary>
    /// Revisión Codex de la #2247, ronda 1: sin la contribución de un módulo declarado, <c>--aplicar</c> podría renombrar
    /// un archivo que una referencia no recogida comparte. La reparación no se crea, y el error nombra el módulo.
    /// </summary>
    [Fact]
    public void Sin_la_contribucion_de_un_modulo_declarado_la_reparacion_no_se_crea()
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton(new ModulosConReferenciasArchivo(["Inbound"]));
        using var proveedor = servicios.BuildServiceProvider();
        using var db = new DbContext(new DbContextOptionsBuilder().Options);

        var crear = () => ReparacionAdjuntosTemporales.Crear(proveedor, db);

        crear.Should().Throw<InvalidOperationException>().WithMessage("*Inbound*no se ejecuta*");
    }

    [Fact]
    public void Sin_la_declaracion_de_modulos_la_reparacion_no_se_crea()
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<IContribucionReferenciasArchivo, CaeManager.Infrastructure.Inbound.FileStorage.ReferenciasArchivoInbound>();
        using var proveedor = servicios.BuildServiceProvider();
        using var db = new DbContext(new DbContextOptionsBuilder().Options);

        var crear = () => ReparacionAdjuntosTemporales.Crear(proveedor, db);

        crear.Should().Throw<InvalidOperationException>().WithMessage("*no declara*");
    }

    /// <summary>Revisión Codex de la #2247, ronda 2: una declaración vacía no deja pasar la reparación sin contribuciones.</summary>
    [Fact]
    public void Con_la_declaracion_de_modulos_vacia_la_reparacion_no_se_crea()
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton(new ModulosConReferenciasArchivo([]));
        using var proveedor = servicios.BuildServiceProvider();
        using var db = new DbContext(new DbContextOptionsBuilder().Options);

        var crear = () => ReparacionAdjuntosTemporales.Crear(proveedor, db);

        crear.Should().Throw<InvalidOperationException>().WithMessage("*vacía*no se ejecuta*");
    }

    /// <summary>
    /// Revisión Codex de la #2247, ronda 2: un segundo registro de la declaración no tapa al primero. Aquí el segundo,
    /// vacío, dejaría fuera a Inbound si solo contara el último.
    /// </summary>
    [Fact]
    public void Con_dos_declaraciones_de_modulos_la_reparacion_no_se_crea()
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton(new ModulosConReferenciasArchivo(["Inbound"]));
        servicios.AddSingleton<IContribucionReferenciasArchivo, CaeManager.Infrastructure.Inbound.FileStorage.ReferenciasArchivoInbound>();
        servicios.AddSingleton(new ModulosConReferenciasArchivo([]));
        using var proveedor = servicios.BuildServiceProvider();
        using var db = new DbContext(new DbContextOptionsBuilder().Options);

        var crear = () => ReparacionAdjuntosTemporales.Crear(proveedor, db);

        crear.Should().Throw<InvalidOperationException>().WithMessage("*más de una vez*no se ejecuta*");
    }

    /// <summary>
    /// La composición real declara una sola vez, con Inbound, y cada módulo declarado aporta su contribución. El proyecto
    /// no expone un <c>WebApplicationFactory</c>: el contenedor se arma con <c>AddInfrastructure</c>, y el barrido de
    /// <c>src</c> de abajo fija que ninguna otra pieza de la composición (Program.cs incluido) registra la declaración ni
    /// contribuciones.
    /// </summary>
    [Fact]
    public void La_composicion_real_declara_una_sola_vez_los_modulos_y_cada_uno_aporta()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());
        using var proveedor = servicios.BuildServiceProvider();

        var declaracion = proveedor.GetServices<ModulosConReferenciasArchivo>().Should().ContainSingle().Subject;
        declaracion.Modulos.Should().Contain("Inbound");
        var aportan = proveedor.GetServices<IContribucionReferenciasArchivo>().Select(c => c.Modulo).ToList();
        declaracion.Modulos.Should().OnlyContain(m => aportan.Contains(m));
    }

    /// <summary>
    /// Barrido de mejor esfuerzo sobre el texto de <c>src</c>: cuenta cada mención de los dos nombres, sea cual sea la
    /// forma del registro (genérico, también partido en líneas, <c>typeof</c>, <c>ServiceDescriptor</c> o fábrica que
    /// nombre el tipo), y exige que solo aparezcan en los ficheros que los definen, consumen, implementan o registran, por
    /// ruta relativa completa. No ve un registro que no nombre ninguno de los dos tipos (por ejemplo, un escaneo de
    /// ensamblados); ese caso solo lo detecta <c>Crear</c> en ejecución.
    /// </summary>
    [Fact]
    public void Solo_la_composicion_de_Infrastructure_registra_la_declaracion_y_las_contribuciones()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer src");
        var mencion = new System.Text.RegularExpressions.Regex(
            @"\b(ModulosConReferenciasArchivo|IContribucionReferenciasArchivo)\b");

        var menciones = Directory.EnumerateFiles(Path.Combine(dir!, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir!, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/"))
            .Select(f => (Fichero: f, Veces: mencion.Matches(File.ReadAllText(Path.Combine(dir!, f))).Count))
            .Where(m => m.Veces > 0)
            .ToDictionary(m => m.Fichero, m => m.Veces);

        menciones.Keys.Should().BeEquivalentTo(
            "src/CaeManager.Infrastructure/FileStorage/ContribucionReferenciasArchivo.cs",
            "src/CaeManager.Infrastructure/FileStorage/ReparacionAdjuntosTemporales.cs",
            "src/CaeManager.Infrastructure/Inbound/FileStorage/ReferenciasArchivoInbound.cs",
            "src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs");
        menciones["src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs"]
            .Should().Be(2, "la composición registra una declaración y la contribución de Inbound, nada más");
        menciones["src/CaeManager.Infrastructure/Inbound/FileStorage/ReferenciasArchivoInbound.cs"]
            .Should().Be(1, "Inbound solo implementa la contribución; no la registra");
    }

    private sealed class EntornoFalso : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
