using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// § 8 de MACRO_PLAN_2026-08-13.md ("Checklist de seguridad como gate duro"):
/// el checklist de <c>CODING_STANDARDS.md</c> § "Checklist de seguridad para
/// módulos nuevos" ya pedía comprobar "alcance de datos en escritura" e "IDs
/// referenciados, cargados bajo el filtro de tenant" — y aun así el PR #190
/// tuvo que corregir exactamente esa clase de fallo dos veces en Comunicaciones:
/// cuatro Commands/Queries resolvían "el buzón genérico del tenant" filtrando
/// <c>ClienteId == null</c> sin notar que un buzón personal de un gestor
/// también cumple esa condición (commit <c>05260a8</c>), y otros ocho puntos de
/// contacto (bandeja, detalle, y seis Commands que cargan una Conversacion
/// existente) tampoco distinguían "triage genuino" de "buzón personal ajeno"
/// (commit <c>b9906ea</c>). El fix centralizado fue
/// <see cref="IAlcanceDatosService.ConexionIntegracionVisibleAsync"/> /
/// <see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/>.
///
/// Este test mecaniza la parte comprobable de ese fix — el mismo criterio que
/// <see cref="AlcanceDeCarteraEnEscrituraTests"/> usa para los 7 agregados con
/// cartera, aplicado aquí a Conversacion/Mensaje: cualquier handler (Command o
/// Query, a diferencia de <see cref="AlcanceDeCarteraEnEscrituraTests"/> que
/// solo mira Commands — la mitad de los puntos de contacto de #190 eran
/// Queries) cuyo request declara <c>ConversacionId</c> o <c>MensajeId</c> (tipo
/// <see cref="Guid"/>, no nulable) Y depende de <see cref="IConversacionRepository"/>
/// o <see cref="IComunicacionesQueryContext"/> para resolverlo, debe depender
/// también de <see cref="IAlcanceDatosService"/>. Igual que el resto de esta
/// familia de tests: comprobar que la dependencia SE LLAMA de verdad (con el
/// resultado correcto) sigue siendo responsabilidad del revisor — esto solo
/// evita que el olvido total de la dependencia pase desapercibido.
///
/// Deliberadamente acotado a <c>ConversacionId</c>/<c>MensajeId</c>: otras
/// propiedades de Id en este namespace (p. ej. <c>SugerenciaVisitaCorreoId</c>
/// en <c>CrearVisitaCommand</c>, o <c>LineaId</c> en los Commands de líneas de
/// WhatsApp — administrador-only por diseño, mismo criterio que
/// <c>ObtenerConexionesIntegracionQueryHandler</c>) no son Conversacion/Mensaje
/// directamente y no son el patrón que #190 encontró — extenderlo a ellas es
/// una decisión de alcance todavía sin tomar, no un olvido de este mecanismo.
/// </summary>
public class AlcanceDeConversacionesYBuzonesTests
{
    [Fact]
    public void Todo_handler_que_carga_una_Conversacion_o_Mensaje_por_Id_comprueba_IAlcanceDatosService()
    {
        var application = typeof(ICommand).Assembly;

        var handlers = application.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                .Select(i => new { Handler = t, Request = i.GetGenericArguments()[0] }))
            .Where(x =>
                x.Request.GetProperty("ConversacionId")?.PropertyType == typeof(Guid) ||
                x.Request.GetProperty("MensajeId")?.PropertyType == typeof(Guid))
            .ToList();

        var infractores = new List<string>();

        foreach (var handlerInfo in handlers)
        {
            var parametros = handlerInfo.Handler.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .ToHashSet();

            var dependeDePersistenciaDeConversacion =
                parametros.Contains(typeof(IConversacionRepository)) ||
                parametros.Contains(typeof(IComunicacionesQueryContext));

            if (!dependeDePersistenciaDeConversacion) continue;
            if (parametros.Contains(typeof(IAlcanceDatosService))) continue;

            infractores.Add($"{handlerInfo.Handler.Name} (request {handlerInfo.Request.Name})");
        }

        infractores.Should().BeEmpty(
            "un handler cuyo request trae ConversacionId/MensajeId y resuelve la Conversacion/Mensaje vía " +
            "IConversacionRepository o IComunicacionesQueryContext debe comprobar IAlcanceDatosService antes de " +
            "actuar — si esto falla, el handler listado puede estar leyendo o escribiendo sobre el buzón personal " +
            "de otro gestor con solo conocer/adivinar el Id (ver PR #190, ResponderConversacionCommandHandler " +
            "como plantilla del fix: alcanceDatos.ConversacionVisibleAsync)");
    }
}
