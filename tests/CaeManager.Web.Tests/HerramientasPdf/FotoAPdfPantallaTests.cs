using System.Buffers.Binary;
using System.Reflection;
using System.Security.Claims;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.HerramientasPdf.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// La pantalla de Foto a PDF con un procesador falso: aquí se prueba la pantalla (subir, ordenar, quitar,
/// descargar y rechazos). Los límites viven en <see cref="ValidadorFotoAPdf"/> y el generador (tests de Application), y la
/// decodificación en el procesador de Infrastructure (<see cref="ProcesadorFotosPdfTests"/>).
/// </summary>
public sealed class FotoAPdfPantallaTests : BunitContext
{
    private static readonly Guid TenantOrigenId = Guid.NewGuid();

    private readonly ProcesadorFalso _procesador = new();

    private readonly PresupuestoMemoriaDocumentos _presupuesto = new(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System);

    /// <summary>El limitador de CPU del proceso; los tests de «ocupado» lo cambian por uno de un hueco y espera corta.</summary>
    private LimitadorConversionesPdf _limitador = new(4, TimeSpan.FromSeconds(10));

    public FotoAPdfPantallaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class UsuarioFalso(string rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(TenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class ProcesadorFalso : IProcesadorFotosPdf
    {
        public List<IReadOnlyList<byte[]>> Composiciones { get; } = [];
        public Result<PdfDeFotos>? Respuesta { get; set; }

        public bool LanzarSinPresupuesto { get; set; }

        /// <summary>Si se pone, la reserva que acompaña al PDF devuelto (para comprobar que la pantalla la devuelve).</summary>
        public IDisposable? ReservaDelPdf { get; set; }

        /// <summary>Lo que recibió el navegador: el flujo que se le pasó, antes de desecharlo.</summary>
        public MemoryStream? FlujoEntregado { get; private set; }

        /// <summary>Si se pone, componer espera aquí: deja la pantalla «generando» el tiempo que haga falta.</summary>
        public ManualResetEventSlim? Pausa { get; set; }

        public Result<PdfDeFotos> ComponerPdf(IReadOnlyList<byte[]> fotos, CancellationToken cancellationToken)
        {
            Composiciones.Add(fotos);
            Pausa?.Wait(TimeSpan.FromSeconds(20), cancellationToken);
            if (LanzarSinPresupuesto)
                throw new PresupuestoMemoriaAgotadoException();
            if (Respuesta is { } r)
                return r;

            FlujoEntregado = new MemoryStream([0x25, 0x50, 0x44, 0x46]);
            return Result.Exito(new PdfDeFotos(FlujoEntregado, ReservaDelPdf ?? new ReservaNula()));
        }

        private sealed class ReservaNula : IDisposable
        {
            public void Dispose()
            {
            }
        }

        /// <summary>Lo que devuelve como miniatura: la pantalla la retiene y reserva su tamaño.</summary>
        public byte[] Miniatura { get; set; } = [1, 2, 3];

        /// <summary>Cuántas de las próximas llamadas a <see cref="CrearMiniatura"/> devuelven un fallo (una foto que el decodificador no lee).</summary>
        public int MiniaturasQueFallan;

        /// <summary>Cuántas veces se llamó a <see cref="CrearMiniatura"/>.</summary>
        public int MiniaturasPedidas;

        public Result<byte[]> CrearMiniatura(byte[] foto, int ladoMaximoPixeles, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref MiniaturasPedidas);
            return Interlocked.Decrement(ref MiniaturasQueFallan) >= 0
                ? Result.Fallo<byte[]>(Error.Crear(ErroresFotoAPdf.FotoIlegible, "x"))
                : Result.Exito(Miniatura);
        }
    }

    private sealed class Disponibilidad(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    private sealed class AutenticacionFalsa(string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "test"))));
    }

    /// <summary>Un JPEG con solo la cabecera que lee el validador (JFIF y SOF0): basta para la pantalla.</summary>
    private static byte[] Jpeg(ushort ancho, ushort alto)
    {
        byte[] sof = [0xFF, 0xC0, 0x00, 0x0B, 0x08, 0, 0, 0, 0, 0x01, 0x01, 0x11, 0x00];
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(5), alto);
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(7), ancho);
        return [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, .. sof, 0xFF, 0xDA];
    }

    private IRenderedComponent<FotoAPdf> Renderizar(bool disponible = true)
    {
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioFalso("GestorCae"));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddSingleton<IProcesadorFotosPdf>(_procesador);
        Services.AddSingleton<IDisponibilidadHerramientasPdf>(new Disponibilidad(disponible));
        Services.AddSingleton<GeneradorPdfDesdeFotos>();
        Services.AddSingleton<ILogger<FotoAPdf>>(_ => NullLogger<FotoAPdf>.Instance);
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa("GestorCae"));
        Services.AddAuthorizationCore();
        Services.AddLocalization();
        Services.AddCascadingAuthenticationState();
        Services.AddSingleton(_ => new CuotaSubidaMasiva(Options.Create(new OpcionesSubidaMasiva
        {
            DirectorioTemporal = Path.Combine(Path.GetTempPath(), "talveg-foto-a-pdf-tests"),
        })));
        Services.AddSingleton<IPresupuestoMemoriaDocumentos>(_presupuesto);
        Services.AddSingleton(_limitador);

        return Render<FotoAPdf>();
    }

    private static async Task SubirAsync(IRenderedComponent<FotoAPdf> cut, params (string Nombre, byte[] Contenido)[] fotos)
    {
        // Sin envolver en cut.InvokeAsync: el manejador espera trabajo en otro hilo (la miniatura) y devolver el control al
        // despachador desde dentro de una invocación suya lo bloquearía.
        await Task.Run(() => cut.FindComponent<InputFile>().UploadFiles(
            fotos.Select(f => InputFileContent.CreateFromBinary(f.Contenido, f.Nombre)).ToArray()));
    }

    private static IReadOnlyList<string> Nombres(IRenderedComponent<FotoAPdf> cut) =>
        cut.FindAll("li.foto-fila .foto-nombre").Select(n => n.TextContent.Trim()).ToList();

    private static void Pulsar(IRenderedComponent<FotoAPdf> cut, string aria)
    {
        cut.WaitForAssertion(() => cut.FindAll($"button[aria-label=\"{aria}\"]").Should().ContainSingle());
        cut.Find($"button[aria-label=\"{aria}\"]").Click();
    }

    /// <summary>
    /// El botón sigue deshabilitado mientras la pantalla termina de añadir fotos (el renglón aparece antes de que acabe la
    /// carga): pulsarlo entonces no hace nada, y bajo carga eso hacía fallar los tests por el reloj. Se espera a que se habilite.
    /// </summary>
    private static void PulsarDescargar(IRenderedComponent<FotoAPdf> cut, string texto)
    {
        cut.WaitForAssertion(() => cut.FindAll("button").Where(b => b.TextContent.Trim() == texto && !b.HasAttribute("disabled"))
            .Should().ContainSingle(), TimeSpan.FromSeconds(20));
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto).Click();
    }

    /// <summary>
    /// Espera a que el procesador reciba las fotos. No se hace con WaitForAssertion: el render que lo comprobaría ocurre al
    /// empezar a generar, antes de que el hilo del procesador llegue a anotar la composición, y no habría otro render que
    /// repitiera la comprobación (bajo carga, el test esperaba el plazo entero y fallaba).
    /// </summary>
    private void EsperarComposicion()
    {
        SpinWait.SpinUntil(() => _procesador.Composiciones.Count > 0, TimeSpan.FromSeconds(20)).Should().BeTrue();
        _procesador.Composiciones.Should().ContainSingle();
    }

    [Fact]
    public void Solo_pueden_entrar_los_roles_de_gestion_y_la_pantalla_pide_autenticacion()
    {
        var autorizar = typeof(FotoAPdf).GetCustomAttribute<AuthorizeAttribute>();
        var roles = autorizar!.Roles!.Split(',');

        roles.Should().BeEquivalentTo(Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae);
        roles.Should().NotContain(Roles.Consulta);
    }

    [Fact]
    public void Se_muestra_la_zona_de_subida_con_los_formatos_y_el_aviso_de_que_no_se_guarda()
    {
        var cut = Renderizar();

        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
        cut.Find(".zona-soltar-archivo-formatos").TextContent.Should().Contain("JPEG o PNG").And.Contain("20 fotos");
        cut.Find("input[type=file]").GetAttribute("accept").Should().Contain("image/jpeg").And.Contain("image/png").And.NotContain("heic");
        cut.Find(".aviso-foto[role=note]").TextContent.Should().Contain("no se guarda").And.Contain("no conserva los datos de la cámara");
        cut.FindAll("li.foto-fila").Should().BeEmpty();
    }

    [Fact]
    public void Si_Herramientas_PDF_no_esta_disponible_no_hay_zona_de_subida()
    {
        var cut = Renderizar(disponible: false);

        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().BeEmpty());
        cut.Markup.Should().NotContain("zona-soltar-archivo");
    }

    [Fact]
    public async Task Las_fotos_subidas_aparecen_en_orden_con_su_miniatura_y_sus_medidas()
    {
        var cut = Renderizar();

        await SubirAsync(cut, ("primera.jpg", Jpeg(4000, 3000)), ("segunda.jpg", Jpeg(3000, 4000)));

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("primera.jpg", "segunda.jpg"));
        cut.FindAll("li.foto-fila .foto-miniatura img").Should().HaveCount(2);
        cut.FindAll("li.foto-fila")[0].TextContent.Should().Contain("Página 1").And.Contain("4000 × 3000 px");
        cut.Find(".barra-acciones-foto").TextContent.Should().Contain("Descargar PDF de 2 páginas");
    }

    [Fact]
    public async Task Una_foto_que_no_es_JPEG_ni_PNG_se_rechaza_por_sus_bytes_aunque_se_llame_foto_jpg()
    {
        var cut = Renderizar();

        await SubirAsync(cut, ("foto.jpg", System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 no soy una foto")));

        cut.WaitForAssertion(() => cut.Find(".aviso-error[role=alert]").TextContent.Should().Contain("foto.jpg").And.Contain("Solo se admiten fotos JPEG y PNG"));
        cut.FindAll("li.foto-fila").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_HEIC_se_rechaza_explicando_que_hacer()
    {
        var cut = Renderizar();
        var heic = new byte[24];
        "ftyp"u8.CopyTo(heic.AsSpan(4));
        "heic"u8.CopyTo(heic.AsSpan(8));

        await SubirAsync(cut, ("IMG_0001.HEIC", heic));

        cut.WaitForAssertion(() => cut.Find(".aviso-error[role=alert]").TextContent.Should().Contain("HEIC").And.Contain("Más compatible"));
        cut.FindAll("li.foto-fila").Should().BeEmpty();
    }

    [Fact]
    public async Task Una_foto_con_demasiados_pixeles_se_rechaza_sin_llegar_a_la_lista()
    {
        var cut = Renderizar();

        await SubirAsync(cut, ("enorme.jpg", Jpeg(30000, 30000)), ("buena.jpg", Jpeg(100, 100)));

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("buena.jpg"));
        cut.Find(".aviso-error[role=alert]").TextContent.Should().Contain("enorme.jpg").And.Contain("megapíxeles");
    }

    [Fact]
    public async Task Subir_mas_fotos_de_las_admitidas_de_una_vez_se_rechaza_con_aviso()
    {
        var cut = Renderizar();

        await SubirAsync(cut, Enumerable.Range(0, LimitesFotoAPdf.MaximoFotos + 1).Select(i => ($"f{i}.jpg", Jpeg(10, 10))).ToArray());

        cut.WaitForAssertion(() => cut.Find(".aviso-error[role=alert]").TextContent.Should().Contain("Solo caben 20 fotos"));
        cut.FindAll("li.foto-fila").Should().BeEmpty();
    }

    [Fact]
    public async Task Al_llegar_al_maximo_la_zona_de_subida_deja_paso_al_aviso_y_quitar_una_la_devuelve()
    {
        var cut = Renderizar();
        await SubirAsync(cut, Enumerable.Range(0, LimitesFotoAPdf.MaximoFotos).Select(i => ($"f{i}.jpg", Jpeg(10, 10))).ToArray());

        cut.WaitForAssertion(() => cut.FindAll("li.foto-fila").Should().HaveCount(LimitesFotoAPdf.MaximoFotos));
        cut.FindAll(".zona-soltar-archivo").Should().BeEmpty();
        cut.Markup.Should().Contain("Has llegado al máximo de 20 fotos");

        Pulsar(cut, "Quitar f3.jpg");

        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo").Should().ContainSingle());
    }

    [Fact]
    public async Task Mover_y_quitar_cambian_el_orden_de_las_paginas()
    {
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)), ("b.jpg", Jpeg(20, 20)), ("c.jpg", Jpeg(30, 30)));
        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("a.jpg", "b.jpg", "c.jpg"));

        Pulsar(cut, "Bajar de posición a.jpg");
        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("b.jpg", "a.jpg", "c.jpg"));

        Pulsar(cut, "Subir de posición c.jpg");
        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("b.jpg", "c.jpg", "a.jpg"));

        Pulsar(cut, "Quitar c.jpg");
        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("b.jpg", "a.jpg"));
    }

    [Fact]
    public async Task Las_flechas_de_los_extremos_estan_deshabilitadas()
    {
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)), ("b.jpg", Jpeg(20, 20)));
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(2));

        cut.Find("button[aria-label=\"Subir de posición a.jpg\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button[aria-label=\"Bajar de posición b.jpg\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button[aria-label=\"Bajar de posición a.jpg\"]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Quitar_todas_vacia_la_lista_y_el_boton_de_descarga()
    {
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)), ("b.jpg", Jpeg(20, 20)));
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(2));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Quitar todas").Click();

        cut.WaitForAssertion(() => cut.FindAll("li.foto-fila").Should().BeEmpty());
        cut.FindAll(".barra-acciones-foto").Should().BeEmpty();
    }

    [Fact]
    public async Task Descargar_manda_al_procesador_las_fotos_en_el_orden_elegido_y_entrega_el_PDF_al_navegador()
    {
        var modulo = JSInterop.SetupModule("./js/foto-a-pdf.js");
        modulo.SetupVoid("descargar", _ => true).SetVoidResult();
        var cut = Renderizar();
        var a = Jpeg(10, 10);
        var b = Jpeg(20, 20);
        await SubirAsync(cut, ("a.jpg", a), ("b.jpg", b));
        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("a.jpg", "b.jpg"));
        Pulsar(cut, "Bajar de posición a.jpg");
        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("b.jpg", "a.jpg"));

        PulsarDescargar(cut, "Descargar PDF de 2 páginas");

        EsperarComposicion();
        _procesador.Composiciones[0].Should().HaveCount(2);
        _procesador.Composiciones[0][0].Should().Equal(b);
        _procesador.Composiciones[0][1].Should().Equal(a);
        cut.WaitForAssertion(() => modulo.Invocations["descargar"].Should().ContainSingle()
            .Which.Arguments[0].Should().Be("fotos.pdf"));
        cut.FindAll(".aviso-error").Should().BeEmpty();
    }

    [Fact]
    public async Task Tras_entregar_el_PDF_al_navegador_se_cierra_el_flujo_y_vuelve_su_reserva_de_memoria()
    {
        var modulo = JSInterop.SetupModule("./js/foto-a-pdf.js");
        modulo.SetupVoid("descargar", _ => true).SetVoidResult();
        _presupuesto.TryReservar(7 * 1024 * 1024, out var reservaDelPdf).Should().BeTrue();
        _procesador.ReservaDelPdf = reservaDelPdf;
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(1));

        PulsarDescargar(cut, "Descargar PDF de 1 página");

        cut.WaitForAssertion(() => modulo.Invocations["descargar"].Should().ContainSingle());
        cut.WaitForAssertion(() => _presupuesto.Reservado.Should().Be(BytesDeLaCadenaDeUnaMiniatura(3),
            "el PDF y lo reservado para él se sueltan juntos al terminar; solo queda la miniatura de la foto que sigue en la lista"));
        _procesador.FlujoEntregado!.CanRead.Should().BeFalse("el PDF no se queda en memoria tras la descarga");
    }

    [Fact]
    public async Task Si_el_procesador_no_encuentra_hueco_en_el_presupuesto_se_avisa_sin_descargar_nada()
    {
        var modulo = JSInterop.SetupModule("./js/foto-a-pdf.js");
        modulo.SetupVoid("descargar", _ => true).SetVoidResult();
        _procesador.LanzarSinPresupuesto = true;
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(1));

        PulsarDescargar(cut, "Descargar PDF de 1 página");

        cut.WaitForAssertion(() => cut.Find(".aviso-error[role=alert]").TextContent.Should().Contain("no hay espacio"));
        modulo.Invocations["descargar"].Should().BeEmpty();
        _presupuesto.Reservado.Should().Be(BytesDeLaCadenaDeUnaMiniatura(3), "la reserva de las fotos leídas también se devuelve; solo queda la miniatura de la foto de la lista");
        Nombres(cut).Should().Equal("a.jpg");
    }

    [Fact]
    public async Task Mientras_se_genera_el_PDF_no_se_pueden_anadir_fotos()
    {
        var modulo = JSInterop.SetupModule("./js/foto-a-pdf.js");
        modulo.SetupVoid("descargar", _ => true).SetVoidResult();
        using var pausa = new ManualResetEventSlim(false);
        _procesador.Pausa = pausa;
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));
        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("a.jpg"), TimeSpan.FromSeconds(20));
        PulsarDescargar(cut, "Descargar PDF de 1 página");
        EsperarComposicion();

        await SubirAsync(cut, ("b.jpg", Jpeg(20, 20)));
        await Task.Delay(500);

        // Sin el cerrojo, b.jpg habría entrado en la lista (y en la copia que ya se está componiendo no estaría).
        Nombres(cut).Should().Equal("a.jpg");
        pausa.Set();
        cut.WaitForAssertion(() => modulo.Invocations["descargar"].Should().ContainSingle(), TimeSpan.FromSeconds(20));
        _procesador.Composiciones.Should().ContainSingle().Which.Should().ContainSingle();
    }

    [Fact]
    public async Task Si_la_composicion_falla_se_muestra_el_motivo_y_no_se_descarga_nada()
    {
        var modulo = JSInterop.SetupModule("./js/foto-a-pdf.js");
        modulo.SetupVoid("descargar", _ => true).SetVoidResult();
        _procesador.Respuesta = Result.Fallo<PdfDeFotos>(Error.Crear(ErroresFotoAPdf.NoSePudoGenerar, "x"));
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(1));

        PulsarDescargar(cut, "Descargar PDF de 1 página");

        cut.WaitForAssertion(() => cut.Find(".aviso-error[role=alert]").TextContent.Should().Contain("No hemos podido crear el PDF"));
        modulo.Invocations["descargar"].Should().BeEmpty();
        Nombres(cut).Should().Equal("a.jpg");
    }

    [Fact]
    public async Task La_miniatura_retenida_reserva_su_tamano_real_y_lo_devuelve_al_quitar_la_foto_y_al_desechar_la_pantalla()
    {
        _procesador.Miniatura = new byte[100];
        var cut = Renderizar();

        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)), ("b.jpg", Jpeg(20, 20)));

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("a.jpg", "b.jpg"));
        _presupuesto.Reservado.Should().Be(2 * BytesDeLaCadenaDeUnaMiniatura(100),
            "cada miniatura que vive en el circuito cuenta con lo que ocupa su cadena data: en UTF-16, no solo los bytes del JPEG");

        Pulsar(cut, "Quitar a.jpg");
        cut.WaitForAssertion(() => _presupuesto.Reservado.Should().Be(BytesDeLaCadenaDeUnaMiniatura(100)));

        await DisposeComponentsAsync();
        _presupuesto.Reservado.Should().Be(0, "desechar la pantalla devuelve las miniaturas que quedaban");
    }

    [Fact]
    public async Task Quitar_todas_devuelve_la_reserva_de_todas_las_miniaturas()
    {
        _procesador.Miniatura = new byte[100];
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)), ("b.jpg", Jpeg(20, 20)));
        cut.WaitForAssertion(() => _presupuesto.Reservado.Should().Be(2 * BytesDeLaCadenaDeUnaMiniatura(100)));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Quitar todas").Click();

        cut.WaitForAssertion(() => _presupuesto.Reservado.Should().Be(0));
    }

    /// <summary>
    /// Lo que se reserva por la cadena <c>data:image/jpeg;base64,...</c> de una miniatura de <paramref name="bytesJpeg"/>
    /// bytes: el Base64 (4 caracteres por cada 3 bytes) más el prefijo, a 3 bytes por carácter (2 de la cadena retenida en
    /// UTF-16 y 1 de su copia UTF-8 en el lote de render pendiente de confirmar).
    /// </summary>
    private static long BytesDeLaCadenaDeUnaMiniatura(int bytesJpeg) =>
        ("data:image/jpeg;base64,".Length + (bytesJpeg + 2) / 3 * 4) * 3L;

    private static int FilasEn(string marcado) => marcado.Split("class=\"foto-fila\"").Length - 1;

    [Fact]
    public async Task La_reserva_de_una_miniatura_quitada_se_mantiene_hasta_despues_del_render_que_la_quita()
    {
        _procesador.Miniatura = new byte[100];
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)), ("b.jpg", Jpeg(20, 20)));
        cut.WaitForAssertion(() => _presupuesto.Reservado.Should().Be(2 * BytesDeLaCadenaDeUnaMiniatura(100)));

        // Lo reservado en el momento en que se pinta cada render (antes de su OnAfterRender, que en Blazor Server llega
        // cuando el navegador confirma el lote).
        var alPintar = new List<(int Filas, long Reservado)>();
        cut.OnMarkupUpdated += (_, _) => alPintar.Add((FilasEn(cut.Markup), _presupuesto.Reservado));

        Pulsar(cut, "Quitar a.jpg");

        cut.WaitForAssertion(() => _presupuesto.Reservado.Should().Be(BytesDeLaCadenaDeUnaMiniatura(100)));
        alPintar.Should().Contain(p => p.Filas == 1);
        alPintar.First(p => p.Filas == 1).Reservado.Should().Be(2 * BytesDeLaCadenaDeUnaMiniatura(100),
            "el render que retira la fila aún lleva la reserva: su lote no está confirmado y la cadena puede seguir serializada");
    }

    [Fact]
    public async Task Sin_hueco_para_convertir_las_fotos_no_entran_y_se_avisa_de_que_se_intente_en_unos_segundos()
    {
        _limitador = new LimitadorConversionesPdf(1, TimeSpan.FromMilliseconds(50));
        using var ocupado = await _limitador.EntrarAsync(CancellationToken.None);
        var cut = Renderizar();

        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)), ("b.jpg", Jpeg(20, 20)));

        cut.WaitForAssertion(() => cut.Find(".aviso-error[role=alert]").TextContent
            .Should().Contain("Hay muchas conversiones en curso, inténtalo en unos segundos"));
        cut.FindAll("li.foto-fila").Should().BeEmpty();
        _procesador.MiniaturasPedidas.Should().Be(0, "sin turno de CPU no se decodifica ninguna foto");
        _presupuesto.Reservado.Should().Be(0, "la foto leída no se queda reservada");
    }

    [Fact]
    public async Task Sin_hueco_para_componer_el_PDF_se_avisa_de_que_se_intente_en_unos_segundos_sin_descargar()
    {
        _limitador = new LimitadorConversionesPdf(1, TimeSpan.FromMilliseconds(50));
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(1));
        using var ocupado = await _limitador.EntrarAsync(CancellationToken.None);
        ocupado.Should().NotBeNull("instrumento: la miniatura devolvió su turno");

        PulsarDescargar(cut, "Descargar PDF de 1 página");

        cut.WaitForAssertion(() => cut.Find(".aviso-error[role=alert]").TextContent
            .Should().Contain("Hay muchas conversiones en curso, inténtalo en unos segundos"));
        _procesador.Composiciones.Should().BeEmpty();
        _procesador.FlujoEntregado.Should().BeNull();
    }

    [Fact]
    public async Task La_miniatura_se_pinta_con_la_misma_cadena_que_se_reservo()
    {
        _procesador.Miniatura = new byte[100];
        var cut = Renderizar();

        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));

        cut.WaitForAssertion(() => cut.FindAll("li.foto-fila .foto-miniatura img").Should().ContainSingle());
        var src = cut.Find("li.foto-fila .foto-miniatura img").GetAttribute("src")!;
        src.Should().Be("data:image/jpeg;base64," + Convert.ToBase64String(new byte[100]));
        (src.Length * 3L).Should().Be(_presupuesto.Reservado,
            "lo reservado es la cadena que retiene el circuito (UTF-16) y su copia en el lote de render (UTF-8)");
    }

    [Fact]
    public async Task Si_el_procesador_no_puede_leer_una_foto_esa_foto_se_rechaza_con_aviso_y_la_pantalla_sigue_con_las_demas()
    {
        _procesador.MiniaturasQueFallan = 1;
        var cut = Renderizar();

        await SubirAsync(cut, ("ilegible.jpg", Jpeg(10, 10)), ("buena.jpg", Jpeg(20, 20)));

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("buena.jpg"));
        cut.WaitForAssertion(() => cut.Find(".lista-rechazadas").TextContent.Should().Contain("ilegible.jpg").And.Contain("No hemos podido leer esta foto"));
        cut.FindAll(".aviso-error").Should().ContainSingle("solo el aviso de las rechazadas: la pantalla no se sustituyó por un error del circuito");
        _presupuesto.Reservado.Should().Be(BytesDeLaCadenaDeUnaMiniatura(3), "la foto rechazada no deja ni su lectura ni su miniatura reservadas");
    }

    [Fact]
    public async Task Una_miniatura_mayor_que_el_tope_no_se_retiene_y_la_foto_entra_sin_vista_previa()
    {
        _procesador.Miniatura = new byte[LimitesFotoAPdf.TamanoMaximoMiniaturaBytes + 1];
        var cut = Renderizar();

        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("a.jpg"));
        cut.FindAll("li.foto-fila .foto-miniatura img").Should().BeEmpty();
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Si_la_miniatura_no_cabe_en_el_presupuesto_la_foto_no_entra_y_se_avisa()
    {
        _procesador.Miniatura = new byte[100];
        // Queda sitio para leer la foto (unas decenas de bytes) pero no para retener la miniatura.
        _presupuesto.TryReservar(_presupuesto.Maximo - 60, out var relleno).Should().BeTrue();
        using var _ = relleno;
        var cut = Renderizar();

        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));

        cut.WaitForAssertion(() => cut.Find(".aviso-error[role=alert]").TextContent.Should().Contain("no hay espacio"));
        cut.FindAll("li.foto-fila").Should().BeEmpty();
        _presupuesto.Reservado.Should().Be(_presupuesto.Maximo - 60, "ni la miniatura ni la foto leída se quedan reservadas");
    }

    [Fact]
    public async Task Descargar_pasa_el_token_del_circuito_tambien_a_la_importacion_del_modulo()
    {
        var modulo = JSInterop.SetupModule("./js/foto-a-pdf.js");
        modulo.SetupVoid("descargar", _ => true).SetVoidResult();
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(1));

        PulsarDescargar(cut, "Descargar PDF de 1 página");

        cut.WaitForAssertion(() => modulo.Invocations["descargar"].Should().ContainSingle());
        var importacion = JSInterop.Invocations.Where(i => i.Identifier == "import" && i.Arguments.Contains("./js/foto-a-pdf.js")).Should().ContainSingle().Subject;
        (importacion.CancellationToken is { CanBeCanceled: true }).Should().BeTrue(
            "sin token, cerrar el circuito durante la importación dejaría la descarga esperando");
    }
}