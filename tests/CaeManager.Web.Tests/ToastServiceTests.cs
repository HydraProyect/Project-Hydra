using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// "Nunca apilar más de 3 visibles simultáneamente" (Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md,
/// "Toasts", P2 #28 de Project-Hydra-Negocio/MATURITY_REVIEW.md). Servicio de C#
/// puro, sin Blazor de por medio — no hace falta bUnit.
/// </summary>
public class ToastServiceTests
{
    [Fact]
    public void Un_cuarto_toast_desplaza_al_mas_antiguo()
    {
        var servicio = new ToastService();

        servicio.Mostrar("primero");
        servicio.Mostrar("segundo");
        servicio.Mostrar("tercero");
        servicio.Mostrar("cuarto");

        servicio.Mensajes.Should().HaveCount(ToastService.MaximoVisibles);
        servicio.Mensajes.Select(m => m.Mensaje).Should().Equal("segundo", "tercero", "cuarto");
    }

    [Fact]
    public void Un_toast_de_error_tambien_puede_ser_desplazado()
    {
        var servicio = new ToastService();

        servicio.Mostrar("error viejo", TonoToast.Error);
        servicio.Mostrar("segundo");
        servicio.Mostrar("tercero");
        servicio.Mostrar("cuarto");

        servicio.Mensajes.Select(m => m.Mensaje).Should().NotContain("error viejo");
    }

    [Fact]
    public void Hasta_tres_toasts_conviven_sin_desplazarse()
    {
        var servicio = new ToastService();

        servicio.Mostrar("primero");
        servicio.Mostrar("segundo");
        servicio.Mostrar("tercero");

        servicio.Mensajes.Select(m => m.Mensaje).Should().Equal("primero", "segundo", "tercero");
    }

    [Fact]
    public void Descartar_quita_el_toast_indicado()
    {
        var servicio = new ToastService();
        servicio.Mostrar("único", TonoToast.Error);
        var id = servicio.Mensajes.Single().Id;

        servicio.Descartar(id);

        servicio.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public async Task EjecutarAccionAsync_invoca_la_accion_y_descarta_el_toast()
    {
        var servicio = new ToastService();
        var ejecutada = false;
        servicio.Mostrar("Elemento eliminado", TonoToast.Exito, "Deshacer", () => { ejecutada = true; return Task.CompletedTask; });
        var id = servicio.Mensajes.Single().Id;

        await servicio.EjecutarAccionAsync(id);

        ejecutada.Should().BeTrue();
        servicio.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void Un_toast_con_accion_expone_el_texto_de_la_accion()
    {
        var servicio = new ToastService();

        servicio.Mostrar("Elemento eliminado", TonoToast.Exito, "Deshacer", () => Task.CompletedTask);

        servicio.Mensajes.Single().TextoAccion.Should().Be("Deshacer");
    }

    /// <summary>
    /// P1-E1: el canal único de avisos no se traga los errores de autorización ni
    /// los de una Sesión Privilegiada — tienen interfaz propia y, cuando llegan como
    /// resultado de una acción, se ven con su propio texto, como error persistente.
    /// Códigos y textos copiados de AutorizacionEscrituraBehavior.
    /// </summary>
    [Theory]
    [InlineData("Autorizacion.SoloLectura", "Tu rol no permite crear, editar ni eliminar datos — solo consultarlos.")]
    [InlineData("Autorizacion.SesionPrivilegiadaSoloLectura", "Un acceso de soporte de plataforma es de solo lectura: no puede crear, editar ni eliminar datos.")]
    public void MostrarError_no_generaliza_los_errores_de_autorizacion_ni_de_Sesion_Privilegiada(string codigo, string mensaje)
    {
        var servicio = new ToastService();

        servicio.MostrarError(CaeManager.Domain.Common.Error.Crear(codigo, mensaje));

        var toast = servicio.Mensajes.Should().ContainSingle().Subject;
        toast.Mensaje.Should().Be(mensaje);
        toast.Tono.Should().Be(TonoToast.Error, "un error no se autodescarta: el usuario tiene que verlo");
    }
    /// <summary>
    /// P1-E2b: el aviso que nombra de qué era la acción («{contexto}: {mensaje}») usa el
    /// mismo canal y tampoco generaliza el texto de autorización ni el de Sesión Privilegiada.
    /// </summary>
    [Theory]
    [InlineData("Autorizacion.SoloLectura", "Tu rol no permite crear, editar ni eliminar datos — solo consultarlos.")]
    [InlineData("Autorizacion.SesionPrivilegiadaSoloLectura", "Un acceso de soporte de plataforma es de solo lectura: no puede crear, editar ni eliminar datos.")]
    [InlineData("Empresa.CifDuplicado", "Ya existe una empresa con ese CIF.")]
    public void MostrarError_con_contexto_pinta_contexto_y_mensaje_literal(string codigo, string mensaje)
    {
        var servicio = new ToastService();

        servicio.MostrarError("Construcciones Norte", CaeManager.Domain.Common.Error.Crear(codigo, mensaje));

        var toast = servicio.Mensajes.Should().ContainSingle().Subject;
        toast.Mensaje.Should().Be($"Construcciones Norte: {mensaje}");
        toast.Tono.Should().Be(TonoToast.Error);
    }

    [Fact]
    public void MostrarError_con_contexto_pone_el_complemento_detras()
    {
        var servicio = new ToastService();

        servicio.MostrarError("No pudimos enviar el correo", CaeManager.Domain.Common.Error.Crear("Correo.Fallo", "El servidor no respondió."),
            "El enlace queda abajo para entregarlo tú mismo.");

        servicio.Mensajes.Single().Mensaje.Should().Be(
            "No pudimos enviar el correo: El servidor no respondió. El enlace queda abajo para entregarlo tú mismo.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MostrarError_con_contexto_vacio_pinta_solo_el_mensaje(string? contexto)
    {
        var servicio = new ToastService();

        servicio.MostrarError(contexto, CaeManager.Domain.Common.Error.Crear("X.Y", "No se pudo."));

        servicio.Mensajes.Single().Mensaje.Should().Be("No se pudo.", "un nombre que no llegó no deja un «: » colgando");
    }
}
