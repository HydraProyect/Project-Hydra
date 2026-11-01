using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Options;
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
