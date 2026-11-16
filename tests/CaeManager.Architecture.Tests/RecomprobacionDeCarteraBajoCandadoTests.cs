using System.Reflection;
using System.Runtime.CompilerServices;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Empresas;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Un escritor que decide con la Asignación de Cartera y después espera un candado tiene que volver a decidir bajo él,
/// sin el alcance memoizado de antes de la espera</b> (issue #2118).
///
/// <para>
/// <c>InvalidacionAlcanceBehavior</c> invalida el alcance solo al empezar el Command, y <c>TransaccionDeComando</c> antes
/// del <c>BEGIN</c>. Si la Asignación de Cartera cambia mientras el Command espera un candado de transacción, la
/// comprobación de antes de la espera, y cualquier otra que lea el alcance memoizado, sigue dando la respuesta vieja. Los
/// candados que se miran aquí son los que un escritor con cartera espera antes de confirmar: el de la regla A-1
/// (<see cref="ISerializacionAltasAcreditacionEmpresa"/>), el de Empresa propia (<see cref="ILimiteEmpresaPropia"/>) y el
/// compartido de cartera (<see cref="IBloqueoCarteraUsuario"/>, cuyo exclusivo lo toma justo quien cambia una cartera).
/// </para>
///
/// <para>
/// <b>Población.</b> Toda clase concreta de Application cuyo constructor recibe uno de esos candados y además
/// <see cref="IAlcanceDatosService"/> o <see cref="IAutoridadAsignacionesService"/>. Cada una tiene que recibir
/// <see cref="IInvalidadorAlcance"/> y alcanzar <see cref="IInvalidadorAlcance.Invalidar"/> desde alguno de sus métodos
/// (grafo de IL, <see cref="GrafoDeLlamadasIl"/>), o figurar en <see cref="Exentas"/> con su motivo.
/// </para>
///
/// <para>
/// <b>Lo que esto NO demuestra.</b> Que la invalidación vaya después de tomar el candado y antes de la comprobación que
/// decide, ni que se vuelva a comprobar la misma fila: eso lo prueban los tests de Application de cada escritor, con un
/// alcance memoizado que se revoca al tomar el candado. Tampoco ve un candado tomado por otro Command enviado por
/// MediatR, ni cierra la carrera entre la última comprobación y el COMMIT: quien revoca una cartera no toma estos
/// candados.
/// </para>
/// </summary>
public class RecomprobacionDeCarteraBajoCandadoTests
{
    private static readonly Type[] Candados =
    [
        typeof(ISerializacionAltasAcreditacionEmpresa),
        typeof(ILimiteEmpresaPropia),
        typeof(IBloqueoCarteraUsuario),
    ];

    private static readonly Type[] Alcances = [typeof(IAlcanceDatosService), typeof(IAutoridadAsignacionesService)];

    /// <summary>Clases de la población que no necesitan recomprobar bajo el candado, con el motivo.</summary>
    private static readonly Dictionary<string, string> Exentas = new(StringComparer.Ordinal)
    {
        ["CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon.FijarResponsableBuzonCommandHandler"] =
            "Solo lo ejecuta el Administrador (lo comprueba el handler antes de la transacción), cuyo alcance es la "
            + "organización entera y no depende de ninguna Asignación de Cartera. La cartera que decide bajo el candado "
            + "compartido es la del responsable, y ResponsableBuzonElegible la lee después de tomarlo, sin pasar por el "
            + "alcance memoizado del actor.",
    };

    /// <summary>
    /// Control del instrumento: escritores que tienen que estar en la población. Si el filtro dejara de verlos (por un
    /// cambio de constructor o de los tipos de arriba), esto se pondría en verde sin mirar nada.
    /// </summary>
    private static readonly string[] DebenEstar =
    [
        "CrearAsignacionCommandHandler",
        "CrearDocumentoCommandHandler",
        "EjecutarImportacionCommandHandler",
        "EjecutarImportacionCombinadaCommandHandler",
        "CrearEmpresaCommandHandler",
        "RestaurarEmpresaCommandHandler",
        "RestaurarClienteCommandHandler",
        "RestaurarSubcontrataCommandHandler",
        "ReasignadorCarteraCliente",
    ];

    [Fact]
    public void Quien_decide_con_la_cartera_y_espera_un_candado_invalida_el_alcance_bajo_el()
    {
        var application = typeof(IInvalidadorAlcance).Assembly;
        var poblacion = ReflexionArquitecturaHelper.TiposDe(application)
            .Where(t => t is { IsClass: true, IsAbstract: false } && !t.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .Where(t => Parametros(t).Any(p => Candados.Contains(p)) && Parametros(t).Any(p => Alcances.Contains(p)))
            .ToList();

        poblacion.Select(t => t.Name).Should().Contain(DebenEstar, "el instrumento tiene que ver a los escritores conocidos");

        var grafo = new GrafoDeLlamadasIl([application]);
        var invalidar = typeof(IInvalidadorAlcance).GetMethod(nameof(IInvalidadorAlcance.Invalidar))!;
        var alcanzan = grafo.QueAlcanzan([invalidar]);

        var incumplen = poblacion
            .Where(t => !Exentas.ContainsKey(t.FullName!))
            .Select(t => (Tipo: t, Recibe: Parametros(t).Contains(typeof(IInvalidadorAlcance)),
                Llama: t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Any(m => alcanzan.Contains(ClaveMetodo.De(m)))))
            .Where(x => !x.Recibe || !x.Llama)
            .Select(x => $"{x.Tipo.FullName}: {(x.Recibe ? "" : "no recibe IInvalidadorAlcance; ")}{(x.Llama ? "" : "no llama a Invalidar()")}")
            .ToList();

        incumplen.Should().BeEmpty(
            "un escritor que decide con la Asignación de Cartera y espera un candado tiene que invalidar el alcance y "
            + "volver a decidir bajo él (issue #2118); si no lo necesita, va a Exentas con su motivo");

        Exentas.Keys.Should().BeSubsetOf(poblacion.Select(t => t.FullName!), "una exención sin clase en la población sobra");
    }

    private static IEnumerable<Type> Parametros(Type tipo) =>
        tipo.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);
}
