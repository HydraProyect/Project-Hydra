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
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Seguimiento de S-C-3 (OWASP API4:2023, ASVS 4.0.3 V12.1.1): abrir la plantilla reserva del presupuesto
/// de memoria de documentos, y sin hueco la generación avisa en vez de tumbar la página.
/// </summary>
public partial class ConfigurarPlantillaEditorGen2Tests
{
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
