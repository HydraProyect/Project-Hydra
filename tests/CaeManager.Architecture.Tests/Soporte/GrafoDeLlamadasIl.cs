using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace CaeManager.Architecture.Tests.Soporte;

/// <summary>
/// Grafo de llamadas leído del IL compilado, para preguntar si un método <b>llama de verdad</b> a
/// otro (directa o transitivamente), no solo si su clase depende del tipo en el constructor.
///
/// <para>
/// <b>Qué cuenta como arista.</b> Toda referencia a un método en el cuerpo IL: <c>call</c>,
/// <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>/<c>ldvirtftn</c> (lambdas y funciones locales
/// pasadas como delegado: una comprobación dentro de una transacción con callback sigue
/// contando) y <c>ldtoken</c> de un método. Un método <c>async</c> o iterador compila a una
/// máquina de estados cuyo cuerpo real es <c>MoveNext</c>; el método original solo la arranca, así
/// que la arista hacia <c>MoveNext</c> se añade a partir de su <see cref="StateMachineAttribute"/>.
/// </para>
///
/// <para>
/// <b>Despacho por interfaz o método abstracto.</b> Un <c>callvirt</c> a un método sin cuerpo
/// (de interfaz o abstracto) cuenta como «llama» solo si <b>todas</b> sus implementaciones en los
/// ensamblados analizados llaman, y hay al menos una. Con «alguna» bastaría con que un servicio
/// cualquiera tuviera una implementación que consultara el objetivo para que todos sus
/// consumidores pasaran por comprobados. Las implementaciones fuera de los ensamblados analizados
/// (dobles de prueba, bibliotecas) no se ven.
/// </para>
///
/// <para>
/// <b>Lo que no ve.</b> Una petición enviada por <c>IMediator.Send</c>: el destino depende del
/// tipo del objeto en tiempo de ejecución y el IL solo nombra <c>ISender.Send&lt;T&gt;</c>. Tampoco
/// una llamada por reflexión ni por un delegado recibido desde fuera del ensamblado analizado. Un
/// handler que delega así no figura como «llama» y tiene que declararse con su motivo.
/// </para>
/// </summary>
internal sealed class GrafoDeLlamadasIl
{
    private const BindingFlags Declarados =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesPorValor = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(o => o.Value)
        .ToDictionary(g => g.Key, g => g.First());

    private readonly HashSet<Module> _modulos;
    private readonly Dictionary<ClaveMetodo, List<ClaveMetodo>> _llamadas = new();
    private readonly Dictionary<ClaveMetodo, List<ClaveMetodo>> _implementaciones = new();
    private readonly HashSet<ClaveMetodo> _sinCuerpo = new();

    public GrafoDeLlamadasIl(IEnumerable<Assembly> ensamblados)
    {
        var lista = ensamblados.Distinct().ToList();
        _modulos = lista.SelectMany(a => a.GetModules()).ToHashSet();

        foreach (var tipo in lista.SelectMany(ReflexionArquitecturaHelper.TiposDe))
        {
            foreach (var metodo in tipo.GetMethods(Declarados).Cast<MethodBase>().Concat(tipo.GetConstructors(Declarados)))
            {
                var clave = ClaveMetodo.De(metodo);
                if (metodo.IsAbstract || metodo.GetMethodBody() is null)
                    _sinCuerpo.Add(clave);

                _llamadas[clave] = ReferenciasDelCuerpo(metodo);
            }

            if (tipo is { IsClass: true, IsAbstract: false })
                RegistrarImplementaciones(tipo);
        }
    }

    /// <summary>
    /// Métodos analizados que alcanzan alguno de <paramref name="objetivos"/>: el menor punto fijo de
    /// «es objetivo, o su cuerpo referencia a uno que alcanza, o no tiene cuerpo y todas sus
    /// implementaciones alcanzan».
    /// </summary>
    public HashSet<ClaveMetodo> QueAlcanzan(IReadOnlyCollection<MethodBase> objetivos)
    {
        var alcanzan = objetivos.Select(ClaveMetodo.De).ToHashSet();
        bool crecio;
        do
        {
            crecio = false;
            foreach (var (metodo, referencias) in _llamadas)
            {
                if (alcanzan.Contains(metodo)) continue;

                var llega = _sinCuerpo.Contains(metodo)
                    ? _implementaciones.TryGetValue(metodo, out var impls) && impls.Count > 0 && impls.All(alcanzan.Contains)
                    : referencias.Any(alcanzan.Contains);

                if (!llega) continue;
                alcanzan.Add(metodo);
                crecio = true;
            }
        } while (crecio);

        return alcanzan;
    }

    /// <summary>
    /// Un camino de <paramref name="desde"/> hasta uno de <paramref name="objetivos"/> que solo pasa por
    /// métodos de <paramref name="alcanzan"/>, legible (<c>Tipo.Método</c>), para que quien revise una
    /// clasificación vea por dónde llega la llamada; vacío si no llega.
    /// </summary>
    public IReadOnlyList<string> Camino(
        MethodBase desde, IReadOnlyCollection<MethodBase> objetivos, IReadOnlySet<ClaveMetodo> alcanzan)
    {
        var finales = objetivos.Select(ClaveMetodo.De).ToHashSet();
        var origen = ClaveMetodo.De(desde);
        if (!alcanzan.Contains(origen)) return [];

        var previo = new Dictionary<ClaveMetodo, ClaveMetodo?> { [origen] = null };
        var cola = new Queue<ClaveMetodo>([origen]);
        while (cola.Count > 0)
        {
            var actual = cola.Dequeue();
            if (finales.Contains(actual))
            {
                var camino = new List<string>();
                for (ClaveMetodo? paso = actual; paso is { } p; paso = previo[p])
                    camino.Add(Nombre(p));
                camino.Reverse();
                return camino;
            }

            var siguientes = _sinCuerpo.Contains(actual)
                ? _implementaciones.GetValueOrDefault(actual) ?? []
                : _llamadas.GetValueOrDefault(actual) ?? [];
            foreach (var siguiente in siguientes.Where(alcanzan.Contains))
            {
                if (previo.TryAdd(siguiente, actual))
                    cola.Enqueue(siguiente);
            }
        }

        return [];
    }

    private string Nombre(ClaveMetodo clave)
    {
        try
        {
            var metodo = clave.Modulo.ResolveMethod(clave.Token);
            return $"{metodo?.DeclaringType?.Name}.{metodo?.Name}";
        }
        catch (ArgumentException)
        {
            return $"0x{clave.Token:X8}";
        }
    }

    private void RegistrarImplementaciones(Type clase)
    {
        foreach (var interfaz in clase.GetInterfaces().Where(i => _modulos.Contains(i.Module)))
        {
            InterfaceMapping mapa;
            try
            {
                mapa = clase.GetInterfaceMap(interfaz);
            }
            catch (ArgumentException)
            {
                continue;
            }

            for (var i = 0; i < mapa.InterfaceMethods.Length; i++)
                Anadir(ClaveMetodo.De(mapa.InterfaceMethods[i]), ClaveMetodo.De(mapa.TargetMethods[i]));
        }

        foreach (var metodo in clase.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!metodo.IsVirtual) continue;
            var basico = metodo.GetBaseDefinition();
            if (basico != metodo && basico.IsAbstract && _modulos.Contains(basico.Module))
                Anadir(ClaveMetodo.De(basico), ClaveMetodo.De(metodo));
        }

        void Anadir(ClaveMetodo declarado, ClaveMetodo implementacion)
        {
            if (declarado == implementacion) return;
            if (!_implementaciones.TryGetValue(declarado, out var lista))
                _implementaciones[declarado] = lista = [];
            if (!lista.Contains(implementacion))
                lista.Add(implementacion);
        }
    }

    private static List<ClaveMetodo> ReferenciasDelCuerpo(MethodBase metodo)
    {
        var resultado = new List<ClaveMetodo>();

        // async / iterador / iterador async: el cuerpo real vive en MoveNext de la máquina de estados.
        foreach (var atributo in metodo.GetCustomAttributes<StateMachineAttribute>(inherit: false))
        {
            var moveNext = atributo.StateMachineType.GetMethod("MoveNext", Declarados);
            if (moveNext is not null)
                resultado.Add(ClaveMetodo.De(moveNext));
        }

        byte[]? il;
        try
        {
            il = metodo.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception e) when (e is InvalidOperationException or BadImageFormatException or NotSupportedException)
        {
            return resultado;
        }

        if (il is null) return resultado;

        var argumentosDeTipo = metodo.DeclaringType is { IsGenericType: true } declarante ? declarante.GetGenericArguments() : null;
        var argumentosDeMetodo = metodo.IsGenericMethod ? metodo.GetGenericArguments() : null;

        var i = 0;
        while (i < il.Length)
        {
            short valor = il[i++];
            if (valor == 0xFE)
                valor = unchecked((short)(0xFE00 | il[i++]));

            if (!OpCodesPorValor.TryGetValue(valor, out var opcode))
                throw new InvalidOperationException(
                    $"Opcode IL desconocido 0x{valor:X} en {metodo.DeclaringType?.FullName}.{metodo.Name}: el lector se ha desalineado");

            if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(il, i);
                MemberInfo? miembro = null;
                try
                {
                    miembro = metodo.Module.ResolveMember(token, argumentosDeTipo, argumentosDeMetodo);
                }
                catch (Exception e) when (e is ArgumentException or BadImageFormatException or TypeLoadException
                                              or FileNotFoundException or MissingMemberException)
                {
                    // Un miembro de un ensamblado que no carga fuera del host (componentes de Blazor):
                    // no puede ser un objetivo de este grafo.
                }

                if (miembro is MethodBase referenciado)
                    resultado.Add(ClaveMetodo.De(referenciado));
            }

            i += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }

        return resultado;
    }
}

/// <summary>
/// Identidad de un método independiente de la instanciación genérica: el módulo y el token de su
/// definición. <c>List&lt;int&gt;.Add</c> y <c>List&lt;string&gt;.Add</c> son el mismo nodo.
/// </summary>
internal readonly record struct ClaveMetodo(Module Modulo, int Token)
{
    public static ClaveMetodo De(MethodBase metodo) => new(metodo.Module, metodo.MetadataToken);
}
