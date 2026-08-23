using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Ninguna interfaz vuelve a agregar la persistencia de todo el sistema.</b>
///
/// <para>
/// P3-32 partió <c>IApplicationDbContext</c> en 32 contratos segregados por
/// feature. Lo que se quiere impedir no es que reaparezca <i>ese nombre</i>:
/// es que reaparezca <i>esa forma</i>.
/// </para>
/// </summary>
public class ProhibicionGodInterfaceTests
{
    /// <summary>
    /// El techo no es un objetivo de diseño: es un cable trampa colocado lejos
    /// de los dos extremos. El contrato más grande de hoy expone <b>14</b>
    /// conjuntos (<c>IComunicacionesQueryContext</c>) y la interfaz-dios que se
    /// retiró rondaría los <b>87</b>, uno por tabla. Cualquier cifra entre medias
    /// distingue las dos cosas; subirla es el acto deliberado que este ratchet
    /// obliga a hacer a la vista.
    /// </summary>
    private const int MaximoDeConjuntosPorContrato = 20;

    [Fact]
    public void IApplicationDbContext_no_existe()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");

        application.GetType("CaeManager.Application.Common.IApplicationDbContext").Should().BeNull(
            "se partió en interfaces segregadas por feature (P3-32, docs/business/MATURITY_REVIEW.md); si reaparece, algo volvió a acoplar todos los agregados en un solo contrato");
    }

    /// <summary>
    /// La misma propiedad, medida en vez de nombrada.
    ///
    /// <para>
    /// El test de arriba vigila una cadena de texto exacta, así que la
    /// regresión se reintroduce sin tocarlo: basta llamarla
    /// <c>IAppDbContext</c>, <c>IDatosContext</c> o ponerla en otro espacio de
    /// nombres. La propiedad no es "ese nombre no existe" — es <b>que ningún
    /// contrato agregue la persistencia de medio sistema</b>, y eso se cuenta.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_contrato_de_persistencia_agrega_medio_sistema()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");

        var conteos = application.GetTypes()
            .Where(t => t.IsInterface)
            .Select(t => (Nombre: t.Name, Conjuntos: ConjuntosExpuestos(t)))
            .Where(x => x.Conjuntos > 0)
            .ToList();

        // Guarda del propio test: si dejara de reconocer los conjuntos, todo lo
        // de abajo pasaría sobre una lista vacía sin observar nada.
        conteos.Should().NotBeEmpty(
            "los contratos de lectura exponen conjuntos consultables; una lista vacía significaría que este " +
            "test ya no sabe reconocerlos");

        var excedidos = conteos
            .Where(x => x.Conjuntos > MaximoDeConjuntosPorContrato)
            .Select(x => $"{x.Nombre} ({x.Conjuntos} conjuntos)")
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, excedidos).Should().BeEmpty(
            $"un contrato que expone más de {MaximoDeConjuntosPorContrato} conjuntos ha dejado de ser el " +
            "contrato de una feature: es la interfaz-dios volviendo con otro nombre. Si la agregación está " +
            "justificada, sube el techo en este mismo commit explicando por qué");
    }

    /// <summary>
    /// Cuenta las propiedades que exponen un conjunto consultable, sea
    /// <c>IQueryable&lt;T&gt;</c> o <c>DbSet&lt;T&gt;</c> — las dos formas
    /// aparecen en los contratos de esta base.
    /// </summary>
    private static int ConjuntosExpuestos(Type interfaz) =>
        interfaz.GetProperties().Count(p =>
            p.PropertyType.IsGenericType
            && p.PropertyType.GetGenericTypeDefinition().Name is "IQueryable`1" or "DbSet`1");
}
