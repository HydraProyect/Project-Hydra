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
/// fallan en CI con el nombre exacto de los tipos que se saltaron la regla.
///
/// Recorren el ensamblado, no los archivos: el nombre del archivo no dice
/// nada de lo que hay dentro. Su primera ejecución lo demostró — los cinco
/// Commands de retención viven en <c>GestionarSolicitudPurgaCommands.cs</c>,
/// fuera de cualquier patrón <c>*Command.cs</c>, y se habrían quedado sin
/// autorizar en silencio.
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

        // Se afirma sobre la lista unida y no sobre la colección: FluentAssertions
        // solo enseña el primer elemento de una colección no vacía, y aquí interesa
        // ver los que falten de golpe (la primera ejecución de este test destapó
        // cinco Commands en un mismo archivo, no uno).
        string.Join(", ", infractores).Should().BeEmpty(
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

        string.Join(", ", infractores).Should()
            .BeEmpty("todo ICommand debe llamarse ...Command (CODING_STANDARDS.md)");
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

        string.Join(", ", infractores).Should().BeEmpty("todo Command devuelve Result o Result<T>");
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

    // Horizonte 2.5 (MACRO_PLAN_2026-08-13.md § 2.5, regla 3): "todo Command
    // de edición declara Version" era revisión manual — CLAUDE.md la
    // documenta, nada la hacía cumplir. La convención de nombre de este repo
    // para "edita un agregado ya existente" es el prefijo Editar (10 de 11
    // Editar*Command actuales ya declaran Guid Version y lo comprueban con
    // ConcurrenciaOptimista.Verificar). RenovarDocumentoCommand se suma
    // explícitamente al patrón por nombre: su propio comentario XML dice
    // "'Editar' un Documento es, en la práctica, renovarlo" — es la edición
    // real de Documento, con otro verbo.
    //
    // Deliberadamente NO se extiende al prefijo Actualizar en general: la
    // mitad de esos Commands son configuración de tenant sin fila propia
    // (ActualizarConfiguracionOperativaCommand/ActualizarParametroSistemaCommand)
    // o interruptores de un único campo booleano sobre TipoDocumento
    // (ActualizarLecturaIaGlobalCommand y similares) — forzar Version ahí
    // sería una regla que no encaja con la realidad, no un hueco. Los tres
    // Actualizar* que sí son la edición completa de un agregado ya lo hacen
    // bien hoy y se listan explícitamente para que una regresión en ellos
    // también se detecte (si no, "Actualizar" quedaría fuera del todo del
    // radar de este test).
    private static readonly HashSet<string> ComandosDeEdicionPorNombreDistinto =
    [
        "ActualizarTarifaClienteCommand", // agregado Tarifa completo, no un campo suelto
        "ActualizarLineaWhatsAppCommand", // agregado ConexionIntegracion completo
        "ActualizarProyectoCommand", // agregado Proyecto completo
    ];

    // RenovarDocumentoCommand (el otro hueco real que dejó Horizonte 2.5,
    // fuera de alcance de aquel PR) ya se corrigió: Documento hereda de
    // EntidadBase, el Command declara Version y el handler la comprueba con
    // ConcurrenciaOptimista.Verificar — ver RenovarDocumentoConcurrenciaTests
    // para el test de punta a punta.
    //
    // EditarTipoDocumentoCommand se investigó en la misma tarea y se decidió
    // NO añadirle Version — es una exclusión deliberada, no un TODO
    // pendiente, así que se documenta aquí en vez de desaparecer del listado
    // sin explicación:
    //
    // - El hueco es de dominio, no solo de Command: TipoDocumento hereda de
    //   EntidadConTenant, no de EntidadBase, así que ni siquiera tiene la
    //   propiedad Version que propagar.
    // - Subir TipoDocumento a EntidadBase no es gratis: además de Version
    //   arrastraría soft delete completo (MarcarComoEliminado/Restaurar,
    //   EstaEliminado, EliminadoEnUtc, EliminadoPorUsuarioId) y
    //   CreadoEnUtc — capacidades que TipoDocumento no usa hoy. No existe
    //   ni un EliminarTipoDocumentoCommand: es un catálogo que se crea y se
    //   edita, nunca se borra. Añadir esa superficie solo para llegar a
    //   Version sería alcance no pedido, no una corrección con margen.
    // - El propio agregado es de bajo riesgo de concurrencia real:
    //   TipoDocumento es configuración de sistema ("Catálogo maestro de
    //   documentos PRL exigibles", ver TipoDocumento.cs), editable solo por
    //   el rol Administrador desde una única pantalla
    //   (TiposDocumento.razor.cs) — no es un agregado que dos gestores CAE
    //   editen a la vez sobre la misma cartera de clientes, como sí lo son
    //   Documento o Cliente. La pérdida silenciosa de una edición aquí es
    //   una molestia recuperable (se vuelve a aplicar el cambio), no una
    //   vigencia mal calculada.
    //
    // Si en el futuro TipoDocumento gana borrado (lógico o no) o pasa a
    // editarse con más frecuencia/concurrencia real, esta exclusión hay que
    // revisitarla — no es una garantía permanente, es la lectura del estado
    // actual del código.
    private static readonly HashSet<string> HuecosConocidosSinVersion =
    [
        "CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento.EditarTipoDocumentoCommand",
    ];

    private static IEnumerable<Type> ComandosDeEdicion() => TiposDeApplication()
        .Where(t => typeof(ICommandBase).IsAssignableFrom(t))
        .Where(t => t.Name.StartsWith("Editar", StringComparison.Ordinal)
                    || t.Name.StartsWith("Renovar", StringComparison.Ordinal)
                    || ComandosDeEdicionPorNombreDistinto.Contains(t.Name));

    [Fact]
    public void Todo_command_de_edicion_declara_Version_para_concurrencia_optimista()
    {
        var infractores = ComandosDeEdicion()
            .Where(t => t.GetProperty("Version")?.PropertyType != typeof(Guid))
            .Select(t => t.FullName!)
            .Where(nombre => !HuecosConocidosSinVersion.Contains(nombre))
            .ToList();

        string.Join(", ", infractores).Should().BeEmpty(
            "todo Command que edita un agregado existente debe declarar 'Guid Version = default' y "
            + "comprobarlo con ConcurrenciaOptimista.Verificar (CLAUDE.md, regla de concurrencia) — "
            + "si el listado de verdad no lo necesita, documenta por qué y añádelo a las exclusiones "
            + "de este test; si de verdad debería tenerlo, es un hueco real (ver HuecosConocidosSinVersion)");
    }

    [Fact]
    public void Los_huecos_conocidos_de_Version_siguen_vigentes()
    {
        // Guarda simétrica de la de arriba: si alguien arregla uno de estos
        // Commands (le añade Version), esta lista queda con una entrada
        // muerta que ya no protege nada — mejor que el test lo note y
        // recuerde borrarla, a que quede ahí indefinidamente como ruido.
        var comandosDeEdicionPorNombre = ComandosDeEdicion().ToDictionary(t => t.FullName!);

        foreach (var hueco in HuecosConocidosSinVersion)
        {
            comandosDeEdicionPorNombre.Should().ContainKey(hueco,
                $"{hueco} ya no aparece entre los Commands de edición detectados — revisa si el hueco sigue vigente");

            comandosDeEdicionPorNombre[hueco].GetProperty("Version")?.PropertyType.Should().NotBe(typeof(Guid),
                $"{hueco} ya tiene Version — retíralo de HuecosConocidosSinVersion, el gate ya lo cubre solo");
        }
    }
}
