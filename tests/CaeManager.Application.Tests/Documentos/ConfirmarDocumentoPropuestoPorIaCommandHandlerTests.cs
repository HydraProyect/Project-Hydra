using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarDocumentoPropuestoPorIa;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// "La IA propone y una persona confirma antes de que exista el Documento"
/// (decisión del propietario, 2026-09-19). Este Command es el único camino por
/// el que una propuesta se convierte en Documento: aquí se fija que solo lo
/// ejecuta una Persona y que lo que crea es lo que la persona confirmó, no lo
/// que propuso la IA.
/// </summary>
public class ConfirmarDocumentoPropuestoPorIaCommandHandlerTests
{
    private static readonly Guid Trabajador = Guid.NewGuid();
    private static readonly Guid Tipo = Guid.NewGuid();
    private static readonly DateOnly Emision = new(2026, 3, 1);

    [Fact]
    public async Task Una_persona_que_confirma_crea_el_Documento_con_las_fechas_confirmadas()
    {
        var mediador = MediadorQueCreaDocumento(out var documentoId);
        var handler = new ConfirmarDocumentoPropuestoPorIaCommandHandler(mediador, ActorPersona());

        var resultado = await handler.Handle(Comando(new PropuestaIaDocumento(Trabajador, Tipo, Emision, null, 96)), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(documentoId);
        var crear = mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>().Subject;
        crear.TrabajadorId.Should().Be(Trabajador);
        crear.TipoDocumentoId.Should().Be(Tipo);
        crear.FechaEmision.Should().Be(Emision, "la fecha es la que la persona confirmó, nunca hoy");
        crear.ArchivoUrl.Should().Be("archivo");
        crear.Comentarios.Should().Be("Creado desde subida múltiple. Propuesta de la IA (96 % de confianza) confirmada por una persona.");
    }

    [Fact]
    public async Task Si_la_persona_corrige_algo_el_Documento_lo_dice()
    {
        var mediador = MediadorQueCreaDocumento(out _);
        var handler = new ConfirmarDocumentoPropuestoPorIaCommandHandler(mediador, ActorPersona());
        var propuesta = new PropuestaIaDocumento(Trabajador, Guid.NewGuid(), Emision.AddDays(-9), null, 80);

        await handler.Handle(Comando(propuesta), CancellationToken.None);

        var crear = mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>().Subject;
        crear.Comentarios.Should().Be("Creado desde subida múltiple. Propuesta de la IA (80 % de confianza) corregida por una persona: tipo, fecha de emisión.");
    }

    [Fact]
    public async Task Si_la_persona_borra_el_vencimiento_propuesto_o_lo_cambia_el_Documento_lo_dice()
    {
        var vencimientoPropuesto = Emision.AddYears(2);
        var propuesta = new PropuestaIaDocumento(Trabajador, Tipo, Emision, vencimientoPropuesto, 90);

        foreach (var (manual, esperado) in new (DateOnly?, string)[]
        {
            (null, "corregida por una persona: fecha de vencimiento."),
            (vencimientoPropuesto.AddDays(-5), "corregida por una persona: fecha de vencimiento."),
            (vencimientoPropuesto, "confirmada por una persona."),
        })
        {
            var mediador = MediadorQueCreaDocumento(out _);
            var handler = new ConfirmarDocumentoPropuestoPorIaCommandHandler(mediador, ActorPersona());

            await handler.Handle(Comando(propuesta) with { FechaVencimientoManual = manual }, CancellationToken.None);

            mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>()
                .Which.Comentarios.Should().EndWith(esperado);
        }
    }

    [Fact]
    public async Task Sin_propuesta_de_la_IA_el_Documento_dice_que_los_datos_los_indico_una_persona()
    {
        var mediador = MediadorQueCreaDocumento(out _);
        var handler = new ConfirmarDocumentoPropuestoPorIaCommandHandler(mediador, ActorPersona());

        await handler.Handle(Comando(PropuestaIaDocumento.Vacia), CancellationToken.None);

        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>()
            .Which.Comentarios.Should().Be("Creado desde subida múltiple. La IA no propuso datos; los indicó una persona.");
    }

    [Fact]
    public async Task Un_servicio_de_fondo_no_puede_confirmar_aunque_traiga_una_identidad_resuelta()
    {
        var mediador = MediadorQueCreaDocumento(out _);
        var handler = new ConfirmarDocumentoPropuestoPorIaCommandHandler(mediador, ActorPersona());

        // Control positivo: sin ámbito declarado, este mismo actor sí confirma (primer test).
        using var ambito = AmbitoActorAuditoria.EstablecerSistema();
        var resultado = await handler.Handle(Comando(new PropuestaIaDocumento(Trabajador, Tipo, Emision, null, 99)), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.ConfirmacionHumanaRequerida");
        mediador.Enviados.Should().BeEmpty("la propuesta de la IA no llega a crear nada sin una persona");
    }

    [Fact]
    public async Task Una_integracion_con_ClaveApi_no_puede_confirmar()
    {
        var mediador = MediadorQueCreaDocumento(out _);
        var handler = new ConfirmarDocumentoPropuestoPorIaCommandHandler(mediador, ActorPersona());

        using var ambito = AmbitoActorAuditoria.EstablecerIntegracionExterna();
        var resultado = await handler.Handle(Comando(new PropuestaIaDocumento(Trabajador, Tipo, Emision, null, 99)), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_identidad_resuelta_no_se_confirma_nada()
    {
        var mediador = MediadorQueCreaDocumento(out _);
        var handler = new ConfirmarDocumentoPropuestoPorIaCommandHandler(mediador, new ActorAuditoriaFalso(ActorAuditoria.SinResolver));

        var resultado = await handler.Handle(Comando(new PropuestaIaDocumento(Trabajador, Tipo, Emision, null, 99)), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue("Desconocido no afirma persona: fallo cerrado");
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task La_fecha_de_emision_es_obligatoria_y_nunca_se_sustituye_por_hoy()
    {
        var validador = new ConfirmarDocumentoPropuestoPorIaCommandValidator();
        var sinFecha = Comando(PropuestaIaDocumento.Vacia) with { FechaEmision = default };

        var resultado = await validador.ValidateAsync(sinFecha);

        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().ContainSingle(e => e.PropertyName == nameof(sinFecha.FechaEmision));
        (await validador.ValidateAsync(Comando(PropuestaIaDocumento.Vacia))).IsValid.Should().BeTrue("control positivo");
    }

    private static ConfirmarDocumentoPropuestoPorIaCommand Comando(PropuestaIaDocumento propuesta) =>
        new(Trabajador, Tipo, Emision, null, "archivo", propuesta);

    private static MediatorFalso MediadorQueCreaDocumento(out Guid documentoId)
    {
        documentoId = Guid.NewGuid();
        return new MediatorFalso { Respuesta = Result.Exito(documentoId) };
    }

    private static ActorAuditoriaFalso ActorPersona() => new(ActorAuditoria.Normal(Guid.NewGuid()));

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
