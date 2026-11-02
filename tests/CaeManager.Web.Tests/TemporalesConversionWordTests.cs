using CaeManager.Infrastructure.Conversion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

public class TemporalesConversionWordTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private LibreOfficeConversorWordPdfService CrearServicio() => new(
        Options.Create(new LibreOfficeConversorWordPdfServiceOptions
        {
            RutaTemporales = _raiz,
            RutaEjecutable = Path.Combine(_raiz, "ejecutable-inexistente")
        }), NullLogger<LibreOfficeConversorWordPdfService>.Instance);

    private LimpiezaTemporalesConversionWordHostedService CrearBarrido() => new(
        Options.Create(new LibreOfficeConversorWordPdfServiceOptions { RutaTemporales = _raiz }),
        NullLogger<LimpiezaTemporalesConversionWordHostedService>.Instance);

    [Fact]
    public void El_proceso_no_hereda_el_entorno_de_la_aplicacion()
    {
        const string variable = "ConnectionStrings__PruebaDeEntornoDeConversion";
        Environment.SetEnvironmentVariable(variable, "secreto");
        try
        {
            var inicio = new System.Diagnostics.ProcessStartInfo("soffice");
            inicio.Environment.Should().ContainKey(variable, "el control: sin limitar, se hereda");

            LibreOfficeConversorWordPdfService.LimitarEntorno(inicio, _raiz);

            inicio.Environment.Should().NotContainKey(variable);
            inicio.Environment["HOME"].Should().Be(_raiz);
            inicio.Environment["TMPDIR"].Should().Be(_raiz);
            inicio.Environment.Should().ContainKey("PATH");
            inicio.Environment.Keys.Should().BeSubsetOf(
                ["PATH", "LANG", "LC_ALL", "SystemRoot", "windir", "HOME", "TMPDIR", "TMP", "TEMP"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task El_perfil_de_la_conversion_lleva_el_endurecimiento()
    {
        var rutaPerfil = Path.Combine(_raiz, "perfil");

        await LibreOfficeConversorWordPdfService.EscribirPerfilEndurecidoAsync(rutaPerfil, CancellationToken.None);

        var registro = await File.ReadAllTextAsync(Path.Combine(rutaPerfil, "user", "registrymodifications.xcu"));
        System.Xml.Linq.XDocument.Parse(registro).Root!.Elements("item").Should().HaveCount(5);
        registro.Should().Contain("""BlockUntrustedRefererLinks" oor:op="fuse"><value>true""")
            .And.Contain("""DisableMacrosExecution" oor:op="fuse"><value>true""")
            .And.Contain("""DisableActiveContent" oor:op="fuse"><value>true""")
            .And.Contain("""MacroSecurityLevel" oor:op="fuse"><value>3""")
            .And.Contain("""Link" oor:op="fuse"><value>2""");
    }

    [Fact]
    public async Task Borra_el_directorio_al_fallar_la_conversion()
    {
        var servicio = CrearServicio();
        var accion = () => servicio.ConvertirAPdfAsync([1, 2, 3]);

        await accion.Should().ThrowAsync<InvalidOperationException>();
        Directory.GetDirectories(_raiz).Should().BeEmpty();
    }

    [Fact]
    public async Task Borra_el_directorio_al_cancelarse()
    {
        var servicio = CrearServicio();
        using var cancelacion = new CancellationTokenSource();
        cancelacion.Cancel();
        var accion = () => servicio.ConvertirAPdfAsync([1, 2, 3], cancelacion.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetDirectories(_raiz).Should().BeEmpty();
    }

    [Fact]
    public async Task Al_arrancar_borra_solo_directorios_de_conversion_antiguos()
    {
        var antiguo = Directory.CreateDirectory(Path.Combine(_raiz, "caemanager-word2pdf-antiguo"));
        File.WriteAllText(Path.Combine(antiguo.FullName, "documento.docx"), "confidencial");
        Directory.SetLastWriteTimeUtc(antiguo.FullName, DateTime.UtcNow.AddHours(-2));
        var reciente = Directory.CreateDirectory(Path.Combine(_raiz, "caemanager-word2pdf-reciente"));
        var ajeno = Directory.CreateDirectory(Path.Combine(_raiz, "otro-directorio"));
        Directory.SetLastWriteTimeUtc(ajeno.FullName, DateTime.UtcNow.AddHours(-2));

        await CrearBarrido().StartAsync(CancellationToken.None);

        Directory.Exists(antiguo.FullName).Should().BeFalse();
        Directory.Exists(reciente.FullName).Should().BeTrue();
        Directory.Exists(ajeno.FullName).Should().BeTrue();
    }

    [Fact]
    public async Task Al_arrancar_no_borra_un_directorio_antiguo_cuya_conversion_sigue_activa()
    {
        var activo = Directory.CreateDirectory(Path.Combine(_raiz, "caemanager-word2pdf-activo"));
        using (new FileStream(Path.Combine(activo.FullName, ".activo"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None))
        {
            Directory.SetLastWriteTimeUtc(activo.FullName, DateTime.UtcNow.AddHours(-2));

            await CrearBarrido().StartAsync(CancellationToken.None);

            Directory.Exists(activo.FullName).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Al_arrancar_borra_tambien_los_restos_antiguos_que_la_version_anterior_dejo_en_el_temporal_global()
    {
        var resto = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"caemanager-word2pdf-{Guid.NewGuid():N}"));
        try
        {
            File.WriteAllText(Path.Combine(resto.FullName, "documento.docx"), "confidencial");
            Directory.SetLastWriteTimeUtc(resto.FullName, DateTime.UtcNow.AddHours(-2));

            await CrearBarrido().StartAsync(CancellationToken.None);

            Directory.Exists(resto.FullName).Should().BeFalse();
        }
        finally
        {
            if (resto.Exists) resto.Delete(recursive: true);
        }
    }

    [Fact]
    public void La_ruta_por_defecto_cuelga_de_la_carpeta_de_la_aplicacion_y_no_del_temporal_global()
    {
        var ruta = Path.GetFullPath(new LibreOfficeConversorWordPdfServiceOptions().RutaTemporales);

        ruta.Should().StartWith(Path.GetFullPath(AppContext.BaseDirectory));
        ruta.Should().NotStartWith(Path.GetFullPath(Path.GetTempPath()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }
}
