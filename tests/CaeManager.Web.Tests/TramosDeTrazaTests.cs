using System.Text;
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
        Directory.Exists(Publicado) ? Directory.GetFiles(Publicado).Select(ruta => Path.GetFileName(ruta)).ToArray() : [];

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

/// <summary>
/// Cuándo graba la traza de TrazaAlFallar (GrabacionDeTraza, sin navegador), con una traza falsa que, como la real en
/// el peor caso, sigue grabando si un corte falla. La propiedad que importa en un repositorio público: lo tecleado
/// durante un inicio de sesión no llega nunca a un fichero publicado, tampoco si el corte previo falló.
/// </summary>
public sealed class GrabacionDeTrazaTests : IDisposable
{
    private const string Acceso = "formulario-de-acceso-con-contraseña";

    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"grabacion-traza-{Guid.NewGuid():N}");
    private readonly TrazaFalsa _traza = new();

    private string Publicado => Path.Combine(_raiz, "publicado");

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }

    private GrabacionDeTraza Nueva() =>
        new(new TramosDeTraza("Clase.Prueba", Publicado, Path.Combine(_raiz, "provisional"), TextWriter.Null, TextWriter.Null), _traza.Operaciones());

    private string[] Publicados() => Directory.Exists(Publicado) ? Directory.GetFiles(Publicado) : [];

    private async Task IniciarSesion(GrabacionDeTraza grabacion)
    {
        await grabacion.PausarAsync();
        _traza.Teclear(Acceso);
        await grabacion.ReanudarAsync();
    }

    [Fact]
    public async Task No_graba_hasta_acabar_el_primer_inicio_de_sesion()
    {
        var grabacion = Nueva();

        await grabacion.PausarAsync();
        _traza.Llamadas.Should().BeEmpty();
        _traza.Teclear(Acceso);
        await grabacion.ReanudarAsync();

        _traza.Llamadas.Should().Equal("Empezar");
        grabacion.Grabando.Should().BeTrue();
    }

    [Fact]
    public async Task Un_test_que_falla_antes_de_iniciar_sesion_solo_deja_el_aviso()
    {
        var grabacion = Nueva();

        await grabacion.TerminarAsync();

        _traza.Llamadas.Should().BeEmpty();
        Publicados().Should().ContainSingle().Which.Should().EndWith("-sin-traza.txt");
        File.ReadAllText(Publicados().Single()).Should().Contain("antes de acabar el inicio de sesión");
    }

    [Fact]
    public async Task Un_test_fallido_guarda_lo_grabado_tras_el_inicio_de_sesion_y_no_el_acceso()
    {
        var grabacion = Nueva();
        await IniciarSesion(grabacion);
        _traza.Teclear("accion-1");

        await grabacion.TerminarAsync();

        _traza.Llamadas.Should().Equal("Empezar", "DetenerGuardando");
        var zip = Publicados().Should().ContainSingle().Subject;
        File.ReadAllText(zip).Should().Contain("accion-1").And.NotContain(Acceso);
    }

    [Fact]
    public async Task Un_segundo_inicio_de_sesion_corta_un_tramo_y_no_graba_el_acceso()
    {
        var grabacion = Nueva();
        await IniciarSesion(grabacion);
        _traza.Teclear("accion-1");
        await IniciarSesion(grabacion);
        _traza.Teclear("accion-2");

        await grabacion.TerminarAsync();

        _traza.Llamadas.Should().Equal("Empezar", "CortarTramo", "EmpezarTramo", "DetenerGuardando");
        Publicados().Should().HaveCount(2).And.OnlyContain(r => r.EndsWith(".zip"));
        Publicados().Select(File.ReadAllText).Should().NotContain(c => c.Contains(Acceso));
        string.Concat(Publicados().Select(File.ReadAllText)).Should().Contain("accion-1").And.Contain("accion-2");
    }

    [Fact]
    public async Task Si_el_corte_falla_el_acceso_que_la_traza_siga_grabando_se_descarta_y_no_se_vuelve_a_grabar()
    {
        var grabacion = Nueva();
        await IniciarSesion(grabacion);
        _traza.Teclear("accion-1");
        _traza.FallarCorte = true;

        await IniciarSesion(grabacion);
        _traza.Teclear("accion-2");
        await grabacion.TerminarAsync();

        // Ni se reanuda (EmpezarTramo) ni se guarda al final (DetenerGuardando): solo se descarta lo que siguiera en marcha.
        _traza.Llamadas.Should().Equal("Empezar", "CortarTramo", "DetenerDescartando");
        grabacion.Grabando.Should().BeFalse();
        Publicados().Should().ContainSingle().Which.Should().EndWith("-sin-traza.txt");
        Publicados().Select(File.ReadAllText).Should().NotContain(c => c.Contains(Acceso));
        Directory.GetFiles(_raiz, "*.zip", SearchOption.AllDirectories).Should().BeEmpty("el ZIP parcial del corte fallido se borra");
    }

    [Fact]
    public async Task Un_test_superado_descarta_la_traza_y_no_deja_ficheros()
    {
        var grabacion = Nueva();
        await IniciarSesion(grabacion);
        _traza.Teclear("accion-1");
        await IniciarSesion(grabacion);
        grabacion.Superada();

        await grabacion.TerminarAsync();

        _traza.Llamadas.Should().Equal("Empezar", "CortarTramo", "EmpezarTramo", "DetenerDescartando");
        Directory.GetFiles(_raiz, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Si_no_se_puede_empezar_a_grabar_el_test_fallido_lo_anota()
    {
        _traza.FallarEmpezar = true;
        var grabacion = Nueva();
        await IniciarSesion(grabacion);

        await grabacion.TerminarAsync();

        grabacion.Grabando.Should().BeFalse();
        File.ReadAllText(Publicados().Single()).Should().Contain("no se pudo empezar a grabar");
    }

    [Fact]
    public async Task Si_detener_la_traza_falla_no_lanza_y_el_test_fallido_lo_anota()
    {
        var grabacion = Nueva();
        await IniciarSesion(grabacion);
        await grabacion.PausarAsync();
        _traza.FallarDescartar = true;

        var terminar = () => grabacion.TerminarAsync();

        await terminar.Should().NotThrowAsync();
        Publicados().Should().Contain(r => r.EndsWith("-tramo1.zip"));
        File.ReadAllText(Publicados().Single(r => r.EndsWith("-sin-traza.txt"))).Should().Contain("no se pudo detener la traza");
    }

    /// <summary>Como la traza de Playwright: lo que se hace mientras un tramo está en marcha queda grabado.</summary>
    private sealed class TrazaFalsa
    {
        private readonly StringBuilder _grabado = new();
        private bool _enMarcha;

        public List<string> Llamadas { get; } = [];
        public bool FallarCorte { get; set; }
        public bool FallarEmpezar { get; set; }
        public bool FallarDescartar { get; set; }

        public void Teclear(string texto)
        {
            if (_enMarcha) _grabado.Append(texto).Append(';');
        }

        public OperacionesDeTraza Operaciones() => new(
            Empezar: () =>
            {
                Llamadas.Add("Empezar");
                if (FallarEmpezar) throw new InvalidOperationException("la traza no arranca");
                _enMarcha = true;
                return Task.CompletedTask;
            },
            EmpezarTramo: () =>
            {
                Llamadas.Add("EmpezarTramo");
                _enMarcha = true;
                return Task.CompletedTask;
            },
            CortarTramo: ruta =>
            {
                Llamadas.Add("CortarTramo");
                if (FallarCorte)
                {
                    // El peor caso: escribe a medias, falla y la traza sigue en marcha.
                    File.WriteAllText(ruta, _grabado.ToString());
                    throw new InvalidOperationException("corte roto");
                }
                Volcar(ruta);
                return Task.CompletedTask;
            },
            DetenerGuardando: ruta =>
            {
                Llamadas.Add("DetenerGuardando");
                Volcar(ruta);
                return Task.CompletedTask;
            },
            DetenerDescartando: () =>
            {
                Llamadas.Add("DetenerDescartando");
                if (FallarDescartar) throw new InvalidOperationException("el navegador ya se cerró");
                _grabado.Clear();
                _enMarcha = false;
                return Task.CompletedTask;
            });

        private void Volcar(string ruta)
        {
            File.WriteAllText(ruta, _grabado.ToString());
            _grabado.Clear();
            _enMarcha = false;
        }
    }
}