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
        public Result<byte[]> Respuesta { get; set; } = Result.Exito(new byte[] { 0x25, 0x50, 0x44, 0x46 });

        /// <summary>Si se pone, componer espera aquí: deja la pantalla «generando» el tiempo que haga falta.</summary>
        public ManualResetEventSlim? Pausa { get; set; }

        public Result<byte[]> ComponerPdf(IReadOnlyList<byte[]> fotos, CancellationToken cancellationToken)
        {
            Composiciones.Add(fotos);
            Pausa?.Wait(TimeSpan.FromSeconds(20), cancellationToken);
            return Respuesta;
        }

        public Result<byte[]> CrearMiniatura(byte[] foto, int ladoMaximoPixeles, CancellationToken cancellationToken) =>
            Result.Exito<byte[]>([1, 2, 3]);
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
        Services.AddSingleton<IPresupuestoMemoriaDocumentos>(new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System));

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

        cut.FindAll("button").First(b2 => b2.TextContent.Trim() == "Descargar PDF de 2 páginas").Click();

        cut.WaitForAssertion(() => _procesador.Composiciones.Should().ContainSingle());
        _procesador.Composiciones[0].Should().HaveCount(2);
        _procesador.Composiciones[0][0].Should().Equal(b);
        _procesador.Composiciones[0][1].Should().Equal(a);
        cut.WaitForAssertion(() => modulo.Invocations["descargar"].Should().ContainSingle()
            .Which.Arguments[0].Should().Be("fotos.pdf"));
        cut.FindAll(".aviso-error").Should().BeEmpty();
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
        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("a.jpg"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Descargar PDF de 1 página").Click();
        cut.WaitForAssertion(() => _procesador.Composiciones.Should().ContainSingle());

        await SubirAsync(cut, ("b.jpg", Jpeg(20, 20)));
        await Task.Delay(500);

        // Sin el cerrojo, b.jpg habría entrado en la lista (y en la copia que ya se está componiendo no estaría).
        Nombres(cut).Should().Equal("a.jpg");
        pausa.Set();
        cut.WaitForAssertion(() => modulo.Invocations["descargar"].Should().ContainSingle());
        _procesador.Composiciones.Should().ContainSingle().Which.Should().ContainSingle();
    }

    [Fact]
    public async Task Si_la_composicion_falla_se_muestra_el_motivo_y_no_se_descarga_nada()
    {
        var modulo = JSInterop.SetupModule("./js/foto-a-pdf.js");
        modulo.SetupVoid("descargar", _ => true).SetVoidResult();
        _procesador.Respuesta = Result.Fallo<byte[]>(Error.Crear(ErroresFotoAPdf.NoSePudoGenerar, "x"));
        var cut = Renderizar();
        await SubirAsync(cut, ("a.jpg", Jpeg(10, 10)));
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(1));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Descargar PDF de 1 página").Click();

        cut.WaitForAssertion(() => cut.Find(".aviso-error[role=alert]").TextContent.Should().Contain("No hemos podido crear el PDF"));
        modulo.Invocations["descargar"].Should().BeEmpty();
        Nombres(cut).Should().Equal("a.jpg");
    }
}
