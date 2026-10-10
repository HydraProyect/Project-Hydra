using CaeManager.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// Traducción a SQL de <see cref="TextoDeBusqueda.Contiene"/>:
/// <c>public.texto_de_busqueda(columna) LIKE public.patron_de_busqueda(término) ESCAPE '\'</c>.
///
/// Las dos funciones las crea la migración <c>AnadeBusquedaSinAcentosEnListados</c>. Va con
/// <c>LIKE</c> y no con <c>strpos</c> (que es lo que emite Npgsql para un <c>Contains</c> cuyo
/// patrón no es un parámetro) porque los índices trigram solo sirven a <c>LIKE</c>; y el
/// término se normaliza y se escapa en PostgreSQL, no en C#, para que columna y término pasen
/// por la misma normalización.
/// </summary>
internal static class MapeoTextoDeBusqueda
{
    private const string Esquema = "public";

    internal static void MapearTextoDeBusqueda(this ModelBuilder builder)
    {
        var contiene = typeof(TextoDeBusqueda).GetMethod(nameof(TextoDeBusqueda.Contiene), [typeof(string), typeof(string)])!;

        builder.HasDbFunction(contiene).HasTranslation(argumentos => new LikeExpression(
            Funcion(TextoDeBusqueda.FuncionSqlTexto, argumentos[0]),
            Funcion(TextoDeBusqueda.FuncionSqlPatron, argumentos[1]),
            new SqlConstantExpression("\\", typeMapping: null),
            typeMapping: null));
    }

    private static SqlFunctionExpression Funcion(string nombre, SqlExpression argumento) => new(
        Esquema,
        nombre,
        [argumento],
        nullable: true,
        argumentsPropagateNullability: [true],
        typeof(string),
        argumento.TypeMapping);
}
