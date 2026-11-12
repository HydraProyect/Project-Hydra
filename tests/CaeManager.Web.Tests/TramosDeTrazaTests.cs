using CaeManager.E2ETests;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lo que TrazaAlFallar (tests/CaeManager.E2ETests) hace con los tramos de la traza de Playwright, sin navegador: un
/// tramo solo llega al directorio que CI publica si el test falla y el tramo quedó completo; un corte fallido no deja
/// ningún fichero; un test superado no deja nada; y cuando falta traza queda un -sin-traza.txt con el motivo (#2194).
/// </summary>
public sealed class TramosDeTrazaTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"tramos-traza-{Guid.NewGuid():N}");
    private readonly StringWriter _salida = new();
    private readonly StringWriter _error = new();

    private string Publicado => Path.Combine(_raiz, "publicado");
    private string Provisional => Path.Combine(_raiz, "provisional");

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }

    private TramosDeTraza Nuevos() => new("Clase.Prueba", Publicado, Provisional, _salida, _error);

    private static Task Escribir(string ruta)
    {
        File.WriteAllText(ruta, "zip completo");
        return Task.CompletedTask;
    }

    private string[] FicherosPublicados() =>
        Directory.Exists(Publicado) ? Directory.GetFiles(Publicado).Select(Path.GetFileName).ToArray()! : [];

    private string[] FicherosProvisionales() =>
        Directory.Exists(Provisional) ? Directory.GetFiles(Provisional, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task El_tramo_se_escribe_fuera_del_directorio_publicado()
    {
        var tramos = Nuevos();
        string? escrita = null;

        await tramos.CortarAsync(ruta => { escrita = ruta; return Escribir(ruta); }, "tramo1");

        escrita.Should().StartWith(Provisional);
        FicherosPublicados().Should().BeEmpty("hasta que el test falla, nada está en el directorio que CI sube");
    }

    [Fact]
    public async Task Un_corte_que_falla_a_medias_borra_el_zip_parcial_y_el_test_fallido_lo_anota()
    {
        var tramos = Nuevos();

        var completo = await tramos.CortarAsync(ruta =>
        {
            File.WriteAllText(ruta, "zip a medias");
            throw new PlaywrightSimulada("el navegador se cerró a media escritura");
        }, "tramo1");

        completo.Should().BeFalse();
        FicherosProvisionales().Should().BeEmpty("un ZIP parcial no puede quedar en ninguna parte");
        tramos.Averia.Should().Contain("tramo1").And.Contain("el navegador se cerró a media escritura");

        var publicadas = tramos.Concluir(superada: false, "sin tramos");

        publicadas.Should().BeEmpty();
        FicherosPublicados().Should().ContainSingle().Which.Should().EndWith("-sin-traza.txt");
        File.ReadAllText(Directory.GetFiles(Publicado).Single()).Should().Contain("el navegador se cerró a media escritura");
        _error.ToString().Should().Contain("incompleta o ausente");
    }

    [Fact]
    public async Task Un_corte_que_termina_sin_escribir_el_tramo_cuenta_como_averia()
    {
        var tramos = Nuevos();

        var completo = await tramos.CortarAsync(_ => Task.CompletedTask, "final");

        completo.Should().BeFalse();
        tramos.Averia.Should().Contain("sin escribir el tramo");
    }

    [Fact]
    public async Task Una_cancelacion_durante_el_corte_borra_el_tramo_y_se_propaga()
    {
        var tramos = Nuevos();

        var cortar = () => tramos.CortarAsync(ruta =>
        {
            File.WriteAllText(ruta, "zip a medias");
            throw new OperationCanceledException();
        }, "tramo1");

        await cortar.Should().ThrowAsync<OperationCanceledException>();
        FicherosProvisionales().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_test_superado_no_deja_ningun_fichero()
    {
        var tramos = Nuevos();
        await tramos.CortarAsync(Escribir, "tramo1");
        await tramos.CortarAsync(Escribir, "final");

        var publicadas = tramos.Concluir(superada: true, "sin tramos");

        publicadas.Should().BeEmpty();
        FicherosPublicados().Should().BeEmpty();
        (Directory.Exists(Provisional) && Directory.EnumerateFileSystemEntries(Provisional).Any())
            .Should().BeFalse("el subdirectorio provisional de la instancia también se borra");
    }

    [Fact]
    public async Task Un_test_superado_con_averia_tampoco_anota_nada()
    {
        var tramos = Nuevos();
        await tramos.CortarAsync(_ => throw new PlaywrightSimulada("fallo"), "tramo1");

        tramos.Concluir(superada: true, "sin tramos");

        FicherosPublicados().Should().BeEmpty();
        _error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_test_fallido_publica_sus_tramos_completos_sin_aviso()
    {
        var tramos = Nuevos();
        await tramos.CortarAsync(Escribir, "tramo1");
        await tramos.CortarAsync(Escribir, "final");

        var publicadas = tramos.Concluir(superada: false, "sin tramos");

        publicadas.Should().HaveCount(2);
        FicherosPublicados().Should().HaveCount(2).And.OnlyContain(f => f.StartsWith("Clase.Prueba-") && f.EndsWith(".zip"));
        FicherosPublicados().Should().Contain(f => f.EndsWith("-tramo1.zip")).And.Contain(f => f.EndsWith("-final.zip"));
        publicadas.Should().OnlyContain(r => File.ReadAllText(r) == "zip completo");
        FicherosProvisionales().Should().BeEmpty();
        _salida.ToString().Should().Contain("Traza de Playwright del test fallido");
        _error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_test_fallido_con_un_tramo_completo_y_otro_fallido_publica_el_completo_y_anota_el_fallo()
    {
        var tramos = Nuevos();
        await tramos.CortarAsync(Escribir, "tramo1");
        await tramos.CortarAsync(ruta => { File.WriteAllText(ruta, "a medias"); throw new PlaywrightSimulada("fallo"); }, "final");

        tramos.Concluir(superada: false, "sin tramos");

        FicherosPublicados().Should().HaveCount(2);
        FicherosPublicados().Should().Contain(f => f.EndsWith("-tramo1.zip")).And.Contain(f => f.EndsWith("-sin-traza.txt"));
        FicherosPublicados().Should().NotContain(f => f.EndsWith("-final.zip"));
    }

    [Fact]
    public void Un_test_fallido_sin_ningun_tramo_deja_el_motivo()
    {
        var tramos = Nuevos();

        tramos.Concluir(superada: false, "el test falló antes de acabar el inicio de sesión");

        FicherosPublicados().Should().ContainSingle().Which.Should().EndWith("-sin-traza.txt");
        File.ReadAllText(Directory.GetFiles(Publicado).Single()).Should().Contain("antes de acabar el inicio de sesión");
    }

    [Fact]
    public void Una_averia_sin_tramos_prevalece_sobre_el_motivo_generico()
    {
        var tramos = Nuevos();
        tramos.Averiar("no se pudo empezar a grabar");
        tramos.Averiar("segundo motivo");

        tramos.Concluir(superada: false, "motivo genérico");

        var aviso = File.ReadAllText(Directory.GetFiles(Publicado).Single());
        aviso.Should().Contain("no se pudo empezar a grabar").And.NotContain("motivo genérico").And.NotContain("segundo motivo");
    }

    private sealed class PlaywrightSimulada(string mensaje) : Exception(mensaje);
}
