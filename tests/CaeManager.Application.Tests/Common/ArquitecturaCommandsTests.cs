using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Red de seguridad de <see cref="ICommand"/> (P1-17 de
/// docs/business/MATURITY_REVIEW.md).
///
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/> autoriza
/// por interfaz, no por nombre. Eso quita el fallo silencioso que tenía la
/// convención de nombre —un typo desactivaba la autorización— pero abre el
/// simétrico: olvidar la interfaz. Estos tests cierran esa vía exigiendo que
/// nombre e interfaz vayan siempre juntos, <b>en ambas direcciones</b>, y
/// fallan en CI con el nombre exacto del tipo que se saltó la regla.
/// </summary>
public class ArquitecturaCommandsTests
{
    private static readonly Assembly EnsambladoApplication = typeof(ICommand).Assembly;

    private static IEnumerable<Type> TiposDeApplication() => EnsambladoApplication
        .GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false });

    [Fact]
    public void Todo_tipo_con_sufijo_Command_implementa_ICommand()
    {
        var infractores = TiposDeApplication()
            .Where(t => t.Name.EndsWith("Command", StringComparison.Ordinal))
            .Where(t => !typeof(ICommandBase).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        infractores.Should().BeEmpty(
            "un Command que no implementa ICommand se salta AutorizacionEscrituraBehavior — "
            + "cualquier rol, incluido uno de solo lectura, podría ejecutarlo");
    }

    [Fact]
    public void Todo_ICommand_se_llama_Command()
    {
        // La otra dirección: un request de escritura con un nombre que no
        // delata que lo es rompe la convención de CODING_STANDARDS.md y hace
        // ilegible el resto del código (los handlers, los validators y la
        // carpeta Commands/ se nombran a partir de él).
        var infractores = TiposDeApplication()
            .Where(t => typeof(ICommandBase).IsAssignableFrom(t))
            .Where(t => !t.Name.EndsWith("Command", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        infractores.Should().BeEmpty("todo ICommand debe llamarse ...Command (CODING_STANDARDS.md)");
    }

    [Fact]
    public void Ningun_Command_devuelve_algo_que_no_sea_Result()
    {
        // Lo garantiza el propio ICommand por construcción; este test protege
        // el caso de que alguien añada un IRequest<T> "de escritura" a mano
        // sin pasar por la interfaz. Importa porque
        // AutorizacionEscrituraBehavior tiene que poder fabricar el fallo:
        // con cualquier otro tipo de respuesta lanza InvalidOperationException
        // en vez de denegar (era el caso real de los dos Commands de
        // importación antes de P1-17).
        var infractores = TiposDeApplication()
            .Where(t => t.Name.EndsWith("Command", StringComparison.Ordinal))
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
                .Select(i => new { Tipo = t, Respuesta = i.GetGenericArguments()[0] }))
            .Where(x => !typeof(Result).IsAssignableFrom(x.Respuesta))
            .Select(x => $"{x.Tipo.FullName} devuelve {x.Respuesta.Name}")
            .ToList();

        infractores.Should().BeEmpty("todo Command devuelve Result o Result<T>");
    }

    [Fact]
    public void Hay_commands_que_inspeccionar()
    {
        // Guarda del propio test: si un cambio de empaquetado dejara la
        // consulta a cero tipos, los tres tests de arriba pasarían vacíos y
        // dejarían de proteger nada sin que nadie se enterara.
        TiposDeApplication()
            .Count(t => typeof(ICommandBase).IsAssignableFrom(t))
            .Should().BeGreaterThan(50);
    }
}
