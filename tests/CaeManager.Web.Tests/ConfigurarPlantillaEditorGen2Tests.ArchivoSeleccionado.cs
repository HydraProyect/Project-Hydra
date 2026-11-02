using System.Reflection;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.CrearPlantillaDocumento;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Plantillas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Hallazgo de la auditoría del 2026-11-02 (OWASP API4:2023, ASVS 4.0.3 V12.1.1): el PDF elegido en el alta de una plantilla
/// se retenía en memoria, sin reserva, hasta pulsar Crear (o para siempre si no se pulsaba). Ahora lo retenido cuenta en el
/// presupuesto del proceso hasta que se sustituye, se crea la plantilla o se cierra la página, y la reserva no espera a la
/// del guardado.
/// </summary>
public partial class ConfigurarPlantillaEditorGen2Tests
{
    private IRenderedComponent<ConfigurarPlantilla> RenderizarAlta(MediatorFalso mediador)
    {
        Registrar(mediador);
        var cut = Render<ConfigurarPlantilla>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Crear y configurar campos"));
        return cut;
    }

    private static Task ElegirArchivoAsync(IRenderedComponent<ConfigurarPlantilla> cut, ArchivoNavegadorFalso archivo)
    {
        var manejador = typeof(ConfigurarPlantilla).GetMethod("ManejarArchivoSeleccionadoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return cut.InvokeAsync(() => (Task)manejador.Invoke(cut.Instance, [new InputFileChangeEventArgs([archivo])])!);
    }

    private static T Campo<T>(IRenderedComponent<ConfigurarPlantilla> cut, string nombre) =>
        (T)typeof(ConfigurarPlantilla).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)!;

    private static void FijarCampo(IRenderedComponent<ConfigurarPlantilla> cut, string nombre, object? valor) =>
        typeof(ConfigurarPlantilla).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, valor);

    /// <summary>Rellena lo que el alta exige además del PDF para poder pulsar Crear.</summary>
    private static void CompletarAlta(IRenderedComponent<ConfigurarPlantilla> cut)
    {
        FijarCampo(cut, "_nombre", "Anexo II");
        FijarCampo(cut, "_tipoDocumentoId", Guid.NewGuid());
        cut.Render();
    }

    private static ArchivoNavegadorFalso Pdf(string nombre = "anexo.pdf") => new(nombre, PdfDeUnaPagina());

    private static Result<CrearPlantillaDocumentoResultadoDto> Creada() =>
        Result.Exito(new CrearPlantillaDocumentoResultadoDto(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public async Task El_PDF_elegido_en_el_alta_cuenta_en_el_presupuesto_mientras_se_retiene_y_al_cerrar_la_pagina_se_devuelve()
    {
        var cut = RenderizarAlta(new MediatorFalso());
        var longitud = PdfDeUnaPagina().Length;

        await ElegirArchivoAsync(cut, Pdf());

        Campo<byte[]?>(cut, "_archivoSeleccionado").Should().NotBeNull();
        _presupuesto.Reservado.Should().Be(longitud, "retenido sin pulsar Crear: no es transitorio y tiene que contarse");

        await cut.Instance.DisposeAsync();

        _presupuesto.Reservado.Should().Be(0, "cerrar la página suelta el archivo");
        Campo<byte[]?>(cut, "_archivoSeleccionado").Should().BeNull();
    }

    [Fact]
    public async Task Sustituir_el_PDF_suelta_el_anterior_antes_de_reservar_el_nuevo()
    {
        var cut = RenderizarAlta(new MediatorFalso());
        var longitud = PdfDeUnaPagina().Length;
        await ElegirArchivoAsync(cut, Pdf("uno.pdf"));
        long reservadoAlLeerElSegundo = -1;

        await ElegirArchivoAsync(cut, new ArchivoNavegadorFalso("dos.pdf", PdfDeUnaPagina())
        {
            AlAbrir = () => reservadoAlLeerElSegundo = _presupuesto.Reservado
        });

        reservadoAlLeerElSegundo.Should().Be(4L * longitud, "al leer el segundo solo está su propia reserva: el primero ya se soltó");
        _presupuesto.Reservado.Should().Be(longitud, "queda un solo archivo retenido");
        await cut.Instance.DisposeAsync();
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_hueco_el_PDF_no_se_retiene_se_avisa_y_no_queda_reservado_nada_propio()
    {
        var cut = RenderizarAlta(new MediatorFalso());
        using var ocupado = await _presupuesto.ReservarAsync(_presupuesto.Maximo - 100);

        await ElegirArchivoAsync(cut, Pdf());

        Toasts.Mensajes.Should().ContainSingle(m => m.Mensaje == PresupuestoMemoriaAgotadoException.MensajeUsuario && m.Tono == TonoToast.Error);
        Campo<byte[]?>(cut, "_archivoSeleccionado").Should().BeNull("sin reserva no se retiene");
        Campo<string?>(cut, "_nombreArchivoSeleccionado").Should().BeNull();
        _presupuesto.Reservado.Should().Be(ocupado.Bytes);
        await cut.Instance.DisposeAsync();
    }

    [Fact]
    public async Task Un_PDF_por_encima_del_maximo_avisa_y_no_reserva_ni_abre_el_flujo()
    {
        var cut = RenderizarAlta(new MediatorFalso());
        var archivo = new ArchivoNavegadorFalso("enorme.pdf", PdfDeUnaPagina()) { TamanoDeclarado = LimitesArchivoSubido.TamanoMaximoBytes + 1L };

        await ElegirArchivoAsync(cut, archivo);

        Toasts.Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error && m.Mensaje.Contains("supera los 10 MB"));
        archivo.Aperturas.Should().Be(0);
        _presupuesto.Reservado.Should().Be(0);
        await cut.Instance.DisposeAsync();
    }

    [Fact]
    public async Task Una_lectura_superada_por_otra_seleccion_se_descarta_y_devuelve_su_reserva()
    {
        var cut = RenderizarAlta(new MediatorFalso());
        var longitud = PdfDeUnaPagina().Length;
        var retener = new TaskCompletionSource();
        var lenta = ElegirArchivoAsync(cut, new ArchivoNavegadorFalso("lenta.pdf", PdfDeUnaPagina()) { Retener = retener });
        _presupuesto.Reservado.Should().Be(4L * longitud, "la lectura lenta ya reservó su pico");

        await ElegirArchivoAsync(cut, Pdf("rapida.pdf"));
        retener.SetResult();
        await lenta;

        Campo<string?>(cut, "_nombreArchivoSeleccionado").Should().Be("rapida.pdf");
        _presupuesto.Reservado.Should().Be(longitud, "solo la selección vigente sigue reservada");
        await cut.Instance.DisposeAsync();
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Una_lectura_en_vuelo_al_cerrar_la_pagina_no_deja_nada_reservado()
    {
        var cut = RenderizarAlta(new MediatorFalso());
        var retener = new TaskCompletionSource();
        var lenta = ElegirArchivoAsync(cut, new ArchivoNavegadorFalso("lenta.pdf", PdfDeUnaPagina()) { Retener = retener });

        await cut.Instance.DisposeAsync();
        retener.TrySetResult();
        await lenta;

        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_suelta_la_reserva_del_PDF_antes_de_guardar_para_no_esperar_a_su_propia_pagina_y_no_retiene_nada_despues()
    {
        long reservadoAlCrear = -1;
        var mediador = new MediatorFalso
        {
            Retener = p =>
            {
                if (p is not CrearPlantillaDocumentoCommand) return null;
                reservadoAlCrear = _presupuesto.Reservado;
                return Task.FromResult<object>(Creada());
            }
        };
        var cut = RenderizarAlta(mediador);
        await ElegirArchivoAsync(cut, Pdf());
        CompletarAlta(cut);

        await Boton(cut, "Crear y configurar campos").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        reservadoAlCrear.Should().Be(0, "el guardado del almacenamiento reserva por su cuenta: la del PDF no puede seguir retenida");
        Campo<byte[]?>(cut, "_archivoSeleccionado").Should().BeNull("creada la plantilla, el PDF vive en el servidor");
        _presupuesto.Reservado.Should().Be(4 * sizeof(char), "solo las páginas del editor recién abierto");
        await cut.Instance.DisposeAsync();
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Si_la_creacion_falla_el_PDF_vuelve_a_retenerse_y_se_puede_reintentar()
    {
        var longitud = PdfDeUnaPagina().Length;
        var mediador = new MediatorFalso
        {
            Retener = p => p is CrearPlantillaDocumentoCommand
                ? Task.FromResult<object>(Result.Fallo<CrearPlantillaDocumentoResultadoDto>(Error.Crear("Plantilla.Prueba", "Fallo de prueba.")))
                : null
        };
        var cut = RenderizarAlta(mediador);
        await ElegirArchivoAsync(cut, Pdf());
        CompletarAlta(cut);

        await Boton(cut, "Crear y configurar campos").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Campo<byte[]?>(cut, "_archivoSeleccionado").Should().NotBeNull("sigue ofreciendo Crear");
        _presupuesto.Reservado.Should().Be(longitud, "y vuelve a contarse");
        Boton(cut, "Crear y configurar campos").HasAttribute("disabled").Should().BeFalse();
        await cut.Instance.DisposeAsync();
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Si_la_creacion_falla_y_no_hay_hueco_para_volver_a_retener_el_PDF_se_descarta_con_aviso()
    {
        ReservaMemoriaDocumentos? ocupado = null;
        var mediador = new MediatorFalso
        {
            Retener = p =>
            {
                if (p is not CrearPlantillaDocumentoCommand) return null;
                // Otra operación se queda con el presupuesto mientras se intenta crear.
                ocupado = _presupuesto.ReservarAsync(_presupuesto.Maximo).AsTask().GetAwaiter().GetResult();
                return Task.FromResult<object>(Result.Fallo<CrearPlantillaDocumentoResultadoDto>(Error.Crear("Plantilla.Prueba", "Fallo de prueba.")));
            }
        };
        var cut = RenderizarAlta(mediador);
        await ElegirArchivoAsync(cut, Pdf());
        CompletarAlta(cut);

        await Boton(cut, "Crear y configurar campos").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Campo<byte[]?>(cut, "_archivoSeleccionado").Should().BeNull("no se retiene lo que no cabe en el presupuesto");
        Toasts.Mensajes.Should().Contain(m => m.Mensaje == PresupuestoMemoriaAgotadoException.MensajeUsuario);
        _presupuesto.Reservado.Should().Be(ocupado!.Bytes);
        ocupado.Dispose();
        await cut.Instance.DisposeAsync();
    }
}
