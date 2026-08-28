using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.DocumentosIa;

/// <summary>
/// D3 (decisión del propietario del producto): un fallo transitorio no debe
/// generar un evento de Sentry por intento — solo al agotar los reintentos,
/// con el historial adjunto. Un fallo definitivo (FileNotFoundException) no
/// tiene reintentos que agotar, así que captura de inmediato. Son dos
/// propiedades distintas, verificadas por separado.
/// </summary>
public class SeguimientoReintentosAnalisisIaTests
{
    private static TrabajoAnalisisDocumento CrearTrabajo() =>
        new(Guid.NewGuid(), Guid.NewGuid(), TipoAnalisisDocumento.VerificacionIa);

    [Fact]
    public void Un_fallo_transitorio_no_captura_mientras_queden_reintentos()
    {
        var alerta = new AlertaOperativaFalsa();
        using var seguimiento = new SeguimientoReintentosAnalisisIa(alerta);
        var trabajo = CrearTrabajo();

        seguimiento.AlEmpezarIntento(trabajo.Id);
        seguimiento.RegistrarFallo(trabajo, new IOException("Timeout llamando al proveedor de IA."));

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Pendiente, "todavía no se alcanzó MaximoIntentos");
        alerta.ExcepcionesCapturadas.Should().BeEmpty("un fallo transitorio con reintentos restantes no debe generar un evento de Sentry");
    }

    [Fact]
    public void Un_fallo_transitorio_captura_UNA_sola_vez_al_agotar_los_reintentos_con_el_historial_adjunto()
    {
        var alerta = new AlertaOperativaFalsa();
        using var seguimiento = new SeguimientoReintentosAnalisisIa(alerta);
        var trabajo = CrearTrabajo();

        for (var intento = 1; intento < TrabajoAnalisisDocumento.MaximoIntentos; intento++)
        {
            seguimiento.AlEmpezarIntento(trabajo.Id);
            seguimiento.RegistrarFallo(trabajo, new IOException($"Fallo transitorio número {intento}."));
        }

        alerta.ExcepcionesCapturadas.Should().BeEmpty("aún quedaba al menos un reintento");

        seguimiento.AlEmpezarIntento(trabajo.Id);
        seguimiento.RegistrarFallo(trabajo, new IOException("Fallo transitorio definitivo."));

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Fallido);
        alerta.ExcepcionesCapturadas.Should().ContainSingle("tres intentos fallidos del mismo trabajo deben generar UNA sola alerta, no tres");
        alerta.ExcepcionesCapturadas[0].MigasDePan.Should().HaveCount(
            TrabajoAnalisisDocumento.MaximoIntentos - 1,
            "los intentos anteriores al que agota deben quedar adjuntos como historial, no perdidos");
    }

    [Fact]
    public void Un_fallo_definitivo_captura_de_inmediato_sin_gastar_reintentos()
    {
        var alerta = new AlertaOperativaFalsa();
        using var seguimiento = new SeguimientoReintentosAnalisisIa(alerta);
        var trabajo = CrearTrabajo();

        seguimiento.AlEmpezarIntento(trabajo.Id);
        seguimiento.RegistrarFallo(trabajo, new FileNotFoundException("No encontramos el archivo solicitado."));

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Fallido, "no tiene sentido reintentar algo que no puede cambiar de resultado");
        trabajo.Intentos.Should().Be(1, "el salto a Fallido es inmediato, no por agotar MaximoIntentos");
        alerta.ExcepcionesCapturadas.Should().ContainSingle("el primer intento ya es el último — no hay reintentos que esperar");
        alerta.ExcepcionesCapturadas[0].MigasDePan.Should().BeEmpty("no hubo intentos previos que dejaran historial");
    }

    [Fact]
    public void Las_migas_de_un_trabajo_no_se_mezclan_con_las_de_otro()
    {
        var alerta = new AlertaOperativaFalsa();
        using var seguimiento = new SeguimientoReintentosAnalisisIa(alerta);
        var trabajoA = CrearTrabajo();
        var trabajoB = CrearTrabajo();

        // Trabajo A: un intento fallido transitorio, sin llegar a agotar — deja una miga y se abandona (p. ej. tuvo éxito en un intento posterior no modelado aquí).
        seguimiento.AlEmpezarIntento(trabajoA.Id);
        seguimiento.RegistrarFallo(trabajoA, new IOException($"Fallo de A para el documento {trabajoA.DocumentoId}."));

        // Trabajo B: distinto Id — debe abrir un ámbito nuevo, no heredar la miga de A.
        for (var intento = 1; intento < TrabajoAnalisisDocumento.MaximoIntentos; intento++)
        {
            seguimiento.AlEmpezarIntento(trabajoB.Id);
            seguimiento.RegistrarFallo(trabajoB, new IOException($"Fallo de B número {intento} para el documento {trabajoB.DocumentoId}."));
        }
        seguimiento.AlEmpezarIntento(trabajoB.Id);
        seguimiento.RegistrarFallo(trabajoB, new IOException($"Fallo de B definitivo para el documento {trabajoB.DocumentoId}."));

        alerta.ExcepcionesCapturadas.Should().ContainSingle("solo B llegó a agotar sus reintentos — A nunca generó evento");
        var migasDeB = alerta.ExcepcionesCapturadas[0].MigasDePan;
        migasDeB.Should().HaveCount(TrabajoAnalisisDocumento.MaximoIntentos - 1);
        migasDeB.Should().OnlyContain(
            miga => miga.Contains(trabajoB.DocumentoId.ToString()),
            "el historial adjunto al evento de B no debe contener migas de A");
    }

    [Fact]
    public void Reintentos_consecutivos_del_mismo_trabajo_reutilizan_el_mismo_ambito()
    {
        var alerta = new AlertaOperativaFalsa();
        using var seguimiento = new SeguimientoReintentosAnalisisIa(alerta);
        var trabajo = CrearTrabajo();

        seguimiento.AlEmpezarIntento(trabajo.Id);
        seguimiento.AlEmpezarIntento(trabajo.Id); // mismo Id que la llamada anterior — no debe abrir un ámbito nuevo.
        seguimiento.RegistrarFallo(trabajo, new IOException("Primer fallo."));

        seguimiento.AlEmpezarIntento(trabajo.Id);
        seguimiento.RegistrarFallo(trabajo, new FileNotFoundException("Segundo fallo, ahora definitivo."));

        alerta.ExcepcionesCapturadas.Should().ContainSingle();
        alerta.ExcepcionesCapturadas[0].MigasDePan.Should().ContainSingle(
            miga => miga.Contains("Primer fallo"),
            "la miga del primer intento debía seguir viva en el mismo ámbito cuando el segundo (definitivo) capturó");
    }
}
