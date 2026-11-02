using System.Reflection;
using CaeManager.Application.Common;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// MJ-08 (plan de mejora del código, 2026-10-31): <b>todo <c>IRequest</c> de Application es un Command o una Query</b>,
/// salvo la lista congelada.
///
/// <para>
/// <b>Por qué.</b> <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/> autoriza por la interfaz marcador
/// <see cref="ICommandBase"/>. <c>ArquitecturaCommandsTests</c> (Application.Tests) cierra las dos direcciones del
/// nombre (<c>*Command</c> ⇔ <c>ICommand</c>), pero no ve un <c>IRequest</c> que no se llame ni <c>*Command</c> ni
/// <c>*Query</c>: si su handler escribe, ningún behavior de escritura lo autoriza y ningún test lo nota. Este test
/// enumera por reflexión todos los <c>IBaseRequest</c> concretos del ensamblado que no son <see cref="ICommandBase"/>
/// ni se llaman <c>*Query</c>, y exige que el conjunto sea exactamente el de <c>Congelados/Requests-sin-ICommand-ni-Query.txt</c>.
/// Un request nuevo de esa clase es una línea que falta (<c>NUEVA</c>): hay que hacerlo <c>ICommand</c> o <c>*Query</c>, o
/// justificarlo en la lista; uno que se migre baja la lista (<c>OBSOLETA</c>).
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados): (1) una <c>*Query</c> cuyo handler escriba —un lector que acaba
/// escribiendo— queda fuera, porque la reflexión sobre tipos no ve el cuerpo del handler; (2) no mide que el
/// <c>ICommand</c> haga además su propia comprobación de alcance (eso es de <c>AlcanceDeCarteraEnEscrituraTests</c> y
/// afines); (3) un <c>INotification</c> con handler que escriba no pasa por este pipeline.
/// </para>
/// </summary>
public class RequestsSinAutorizacionDeEscrituraCongeladosTests
{
    private const string Lista = "Requests-sin-ICommand-ni-Query";

    private const string Guia =
        "Un IRequest que no es Command ni Query se salta AutorizacionEscrituraBehavior: si su handler escribe, " +
        "cualquier rol podría ejecutarlo. NUEVA: hazlo ICommand (sufijo Command) si escribe, o *Query si solo lee; solo si " +
        "es de verdad otra cosa, añade la línea a tests/CaeManager.Architecture.Tests/Congelados/" + Lista + ".txt con un " +
        "comentario '#' que diga por qué. OBSOLETA: lo has migrado; borra la línea.";

    private static readonly Assembly Application = typeof(ICommandBase).Assembly;

    [Fact]
    public void Todo_IRequest_de_Application_es_Command_o_Query_salvo_la_lista_congelada()
    {
        var fallo = ListaCongelada.Verificar(Lista, Medir(Application), Guia);

        fallo.Should().BeNull();
    }

    [Fact]
    public void El_escaner_ve_los_Commands_y_las_Queries_de_Application()
    {
        // Control positivo independiente de la lista: si la reflexión no viera Commands y Queries, «ningún request
        // suelto» valdría por vacío.
        var requests = RequestsConcretos(Application).ToList();

        requests.Count(t => typeof(ICommandBase).IsAssignableFrom(t)).Should().BeGreaterThan(100);
        requests.Count(t => t.Name.EndsWith("Query", StringComparison.Ordinal)).Should().BeGreaterThan(100);
    }

    [Fact]
    public void El_detector_separa_Commands_Queries_y_requests_sueltos()
    {
        // Tipos sintéticos: el detector se prueba con fuentes propias, nunca con una entrada de la lista.
        EsRequestSuelto(typeof(RequestSueltoSintetico)).Should().BeTrue();
        EsRequestSuelto(typeof(ConsultaSinteticaQuery)).Should().BeFalse();
        EsRequestSuelto(typeof(OrdenSinteticaCommand)).Should().BeFalse();
    }

    internal static IReadOnlyDictionary<Ubicacion, int> Medir(Assembly ensamblado) =>
        RequestsConcretos(ensamblado)
            .Where(EsRequestSuelto)
            .ToDictionary(t => new Ubicacion(t.Namespace ?? "(sin espacio de nombres)", NombreLegible(t)), _ => 1);

    private static IEnumerable<Type> RequestsConcretos(Assembly ensamblado) =>
        ensamblado.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IBaseRequest).IsAssignableFrom(t));

    private static bool EsRequestSuelto(Type t) =>
        !typeof(ICommandBase).IsAssignableFrom(t) && !t.Name.EndsWith("Query", StringComparison.Ordinal);

    private static string NombreLegible(Type t) => t.DeclaringType is { } padre ? $"{padre.Name}+{t.Name}" : t.Name;

    private sealed record RequestSueltoSintetico : IRequest<int>;

    private sealed record ConsultaSinteticaQuery : IRequest<int>;

    private sealed record OrdenSinteticaCommand : ICommand;
}
