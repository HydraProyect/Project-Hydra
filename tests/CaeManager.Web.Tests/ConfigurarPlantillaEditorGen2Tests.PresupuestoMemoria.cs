using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Plantillas.Queries.DetectarCamposPlantilla;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillaDocumentoVersion;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Plantillas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Seguimiento de S-C-3 (OWASP API4:2023, ASVS 4.0.3 V12.1.1): abrir la plantilla reserva del presupuesto
/// de memoria de documentos, y sin hueco la generación avisa en vez de tumbar la página.
/// </summary>
public partial class ConfigurarPlantillaEditorGen2Tests
{
    private readonly PresupuestoMemoriaDocumentos _presupuesto = new(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = 100_000, EsperaMaxima = TimeSpan.FromMilliseconds(50) }),
        TimeProvider.System);

    /// <summary>
    /// Revisión de Codex del 2026-11-01 (OWASP API4:2023, ASVS 4.0.3 V12.1.1): el editor conserva el base64 de las páginas
    /// rasterizadas mientras está abierto, y lo reserva; cerrarlo lo devuelve. La copia del PDF solo se reserva mientras
    /// se inicia el editor.
    /// </summary>
    [Fact]
    public async Task El_editor_abierto_retiene_el_base64_de_sus_paginas_y_al_cerrarse_lo_devuelve()
    {
        var cut = Renderizar(new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) });

        // El rasterizador falso devuelve 2 bytes -> "iVA=" (4 caracteres de 2 bytes cada uno).
        _presupuesto.Reservado.Should().Be(4 * sizeof(char), "solo queda lo que el editor retiene: la copia del PDF ya se soltó");

        await cut.Instance.DisposeAsync();

        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_hueco_para_las_paginas_el_editor_no_se_abre_avisa_y_no_deja_nada_reservado()
    {
        // Hueco justo para la copia del PDF (cuatro veces su longitud) y ni un byte más para el base64 de la página.
        var ocupado = await _presupuesto.ReservarAsync(
            _presupuesto.Maximo - 4L * AlmacenamientoFalso.LongitudPdf);

        var cut = Renderizar(new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) });

        Toasts.Mensajes.Should().Contain(m => m.Mensaje == PresupuestoMemoriaAgotadoException.MensajeUsuario && m.Tono == TonoToast.Error);
        _presupuesto.Reservado.Should().Be(ocupado.Bytes, "ni la copia del PDF ni las páginas se quedan reservadas");
        ocupado.Dispose();
        await cut.Instance.DisposeAsync();
    }

    /// <summary>
    /// Revisión de Codex del 2026-11-01: si la detección inicial lanza tras crear las páginas, el editor muestra el error
    /// de carga y no puede seguir reteniendo las páginas ni su reserva hasta cerrar la página.
    /// </summary>
    [Fact]
    public async Task Si_la_deteccion_inicial_lanza_tras_crear_las_paginas_se_sueltan_con_su_reserva()
    {
        var mediador = new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, estado: EstadoConfiguracionPlantilla.Borrador, elementos: [])),
            Retener = p => p is DetectarCamposPlantillaQuery
                ? Task.FromException<object>(new InvalidOperationException("Fallo simulado de la detección."))
                : null
        };

        var cut = Renderizar(mediador);

        cut.Find("[role='alert']").TextContent.Should().Contain("No pudimos cargar esta plantilla");
        cut.FindAll(".editor-plantilla-pagina").Should().BeEmpty("el editor no se muestra con el error de carga");
        _presupuesto.Reservado.Should().Be(0, "ni la copia del PDF ni las páginas rasterizadas se quedan reservadas");
        await cut.Instance.DisposeAsync();
    }

    /// <summary>
    /// Revisión de Codex del 2026-11-01: tras una carga rechazada el contenedor del lienzo se destruye, y el reintento con
    /// éxito crea uno nuevo sin los eventos de arrastre: el módulo JS se inicia de nuevo sobre él.
    /// </summary>
    [Fact]
    public async Task Tras_una_carga_rechazada_y_un_reintento_con_exito_el_modulo_se_inicia_de_nuevo_sobre_el_contenedor_nuevo()
    {
        var mediador = new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) };
        var cut = Renderizar(mediador);
        IniciosDelModulo().Should().Be(1);

        var otraVersionId = Guid.NewGuid();
        mediador.Version = Result.Fallo<PlantillaDocumentoVersionDetalleDto>(Error.Crear("Plantilla.Prueba", "Fallo de prueba que no es una ausencia."));
        cut.Render(p => p.Add(c => c.PlantillaDocumentoVersionId, otraVersionId));
        cut.WaitForAssertion(() => cut.Find("[role='alert']"));

        mediador.Version = Result.Exito(Detalle(otraVersionId));
        await Boton(cut, "Reintentar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".editor-plantilla-pagina").Should().NotBeEmpty());

        IniciosDelModulo().Should().Be(2, "el contenedor es otro elemento del DOM y necesita sus propios eventos");
        await cut.Instance.DisposeAsync();
    }

    private int IniciosDelModulo() => _moduloJs.Invocations.Count(i => i.Identifier == "iniciar");

    /// <summary>
    /// Segunda revisión de Codex del 2026-11-01: una carga A que espera la detección inicial puede fallar después de que
    /// otra carga B montase sus páginas. La limpieza del error pertenece a la carga que las creó: la de A, ya superada,
    /// no puede soltar las reservas ni vaciar las páginas de B.
    /// </summary>
    [Fact]
    public async Task Una_carga_superada_que_falla_no_suelta_las_paginas_de_la_carga_que_las_monto_despues()
    {
        var deteccion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var versionB = Guid.NewGuid();
        var mediador = new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, estado: EstadoConfiguracionPlantilla.Borrador, elementos: [])),
            Retener = p => p is DetectarCamposPlantillaQuery ? deteccion.Task : null
        };
        Registrar(mediador);
        var cut = Render<ConfigurarPlantilla>(p => p.Add(c => c.PlantillaDocumentoVersionId, _versionId));
        cut.WaitForAssertion(() => mediador.Veces<DetectarCamposPlantillaQuery>().Should().Be(1), TimeSpan.FromSeconds(10));

        // B sustituye a A y monta sus páginas mientras A sigue esperando la detección.
        mediador.Version = Result.Exito(Detalle(versionB));
        cut.Render(p => p.Add(c => c.PlantillaDocumentoVersionId, versionB));
        cut.WaitForAssertion(() => cut.FindAll(".editor-plantilla-pagina").Should().HaveCount(1), TimeSpan.FromSeconds(10));
        // Las páginas de B, y la copia del PDF de A, que A retiene mientras espera la detección.
        _presupuesto.Reservado.Should().Be(4 * sizeof(char) + 4L * AlmacenamientoFalso.LongitudPdf);

        deteccion.SetException(new InvalidOperationException("Fallo simulado de la detección de A."));
        await Task.Delay(300);
        await cut.InvokeAsync(() => { });

        cut.FindAll(".editor-plantilla-pagina").Should().HaveCount(1, "el fallo de A no vacía las páginas de B");
        cut.FindAll("[role='alert']").Should().BeEmpty("el fallo de una carga superada no es un error de la vigente");
        _presupuesto.Reservado.Should().Be(4 * sizeof(char), "A no libera las reservas de B");
        await cut.Instance.DisposeAsync();
        _presupuesto.Reservado.Should().Be(0);
    }

    /// <summary>
    /// Segunda revisión de Codex del 2026-11-01: con la importación del módulo JS pendiente, un render sin editor reinicia
    /// el estado y el siguiente editor pide otro inicio. La importación es una sola y `iniciar` se llama una vez para el
    /// contenedor vigente, no una por render que la esperase.
    /// </summary>
    [Fact]
    public async Task Con_la_importacion_del_modulo_pendiente_no_se_importa_dos_veces_ni_se_inicia_dos_veces()
    {
        var mediador = new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) };
        Registrar(mediador);
        var js = new JsRuntimeConImportacionRetenida(JSInterop.JSRuntime);
        Services.AddSingleton<IJSRuntime>(js);
        var modulo = new ModuloJsFalso();

        var cut = Render<ConfigurarPlantilla>(p => p.Add(c => c.PlantillaDocumentoVersionId, _versionId));
        cut.WaitForAssertion(() => cut.FindAll(".editor-plantilla-pagina").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => js.Importaciones.Should().Be(1), TimeSpan.FromSeconds(10));

        // Un render sin editor (carga rechazada) mientras la importación sigue pendiente, y después un editor nuevo.
        var otraVersionId = Guid.NewGuid();
        mediador.Version = Result.Fallo<PlantillaDocumentoVersionDetalleDto>(Error.Crear("Plantilla.Prueba", "Fallo de prueba que no es una ausencia."));
        cut.Render(p => p.Add(c => c.PlantillaDocumentoVersionId, otraVersionId));
        cut.WaitForAssertion(() => cut.Find("[role='alert']"), TimeSpan.FromSeconds(10));

        mediador.Version = Result.Exito(Detalle(otraVersionId));
        await Boton(cut, "Reintentar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".editor-plantilla-pagina").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        await cut.InvokeAsync(() => { });

        js.Importaciones.Should().Be(1, $"la importación pendiente se comparte, no se repite (inicios={modulo.Inicios})");

        js.Importacion.SetResult(modulo);
        for (var intento = 0; intento < 100 && modulo.Inicios == 0; intento++)
        {
            await cut.InvokeAsync(() => { });
            await Task.Delay(50);
        }
        modulo.Inicios.Should().Be(1, $"importaciones={js.Importaciones} completada={js.Importacion.Task.IsCompleted}");
        await Task.Delay(200);
        modulo.Inicios.Should().Be(1, $"el contenedor vigente se inicia una sola vez (importaciones={js.Importaciones})");

        await cut.Instance.DisposeAsync();
    }

    /// <summary>Delega todo en el tiempo de ejecución de bUnit salvo la importación del módulo del editor, que retiene.</summary>
    private sealed class JsRuntimeConImportacionRetenida(IJSRuntime real) : IJSRuntime
    {
        public TaskCompletionSource<IJSObjectReference> Importacion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Importaciones { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            EsImportacion(identifier, args) ? Esperar<TValue>() : real.InvokeAsync<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            EsImportacion(identifier, args) ? Esperar<TValue>() : real.InvokeAsync<TValue>(identifier, cancellationToken, args);

        private bool EsImportacion(string identifier, object?[]? args) =>
            identifier == "import" && args is [Modulo, ..];

        private async ValueTask<TValue> Esperar<TValue>()
        {
            Importaciones++;
            return (TValue)(object)await Importacion.Task;
        }
    }

    private sealed class ModuloJsFalso : IJSObjectReference
    {
        public int Inicios { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "iniciar") Inicios++;
            return default;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => default;
    }

    [Fact]
    public async Task Sin_hueco_en_el_presupuesto_de_memoria_la_generacion_avisa_y_se_puede_reintentar()
    {
        var trabajadorId = Guid.NewGuid();
        var mediador = new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, estado: EstadoConfiguracionPlantilla.Confirmada,
                elementos: [Elemento("Observaciones", fuente: FuenteDatoPlantilla.Manual)])),
            Retener = p => p switch
            {
                ObtenerTrabajadoresParaSelectorQuery => Task.FromResult<object>(
                    (IReadOnlyList<TrabajadorSelectorDto>)[new TrabajadorSelectorDto(trabajadorId, "Ana Pérez", null, null)]),
                GenerarDocumentoIndividualCommand => Task.FromException<object>(new PresupuestoMemoriaAgotadoException()),
                _ => null
            }
        };
        var cut = Renderizar(mediador);
        await cut.FindComponents<CampoSelect>().First(c => c.Instance.Etiqueta == "Trabajador")
            .Find("select").ChangeAsync(new ChangeEventArgs { Value = trabajadorId.ToString() });

        await Boton(cut, "Generar documento").ClickAsync(new MouseEventArgs());

        Toasts.Mensajes.Should().ContainSingle()
            .Which.Should().Match<ToastMensaje>(m => m.Mensaje == PresupuestoMemoriaAgotadoException.MensajeUsuario && m.Tono == TonoToast.Error);
        Boton(cut, "Generar documento").HasAttribute("disabled").Should().BeFalse("el aviso deja reintentar");
    }
}
