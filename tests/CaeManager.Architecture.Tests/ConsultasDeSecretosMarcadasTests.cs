using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>IConsultaDeSecretosDeTenant</c> es una lista, y las listas se quedan
/// obsoletas solas. Los dos primeros tests son su ratchet: uno por reflexión
/// sobre lo que las Queries devuelven, otro por texto sobre lo que leen.
///
/// Lo que protegen: una sesión de <c>SoporteLectura</c> ve el tenant entero, y
/// la única razón por la que no ve sus contraseñas de plataformas externas es
/// que esas dos Queries están marcadas. Una Query nueva de credenciales sin
/// marcar no rompería nada visible — simplemente entregaría las llaves, en
/// silencio, el día que exista el camino que abre sesiones.
///
/// <para>
/// <b>Contrato efectivo de <see cref="Toda_consulta_de_secretos_acota_por_cartera"/>,
/// más estrecho que su nombre (REC-153):</b> solo comprueba que el handler
/// reciba <c>IAlcanceDatosService</c> por constructor — nunca qué método del
/// alcance invoca. No distingue el alcance de LECTURA del de GESTIÓN, así que
/// una consulta marcada y con el servicio inyectado puede seguir usando
/// <c>EmpresaVisibleAsync</c>/<c>SubcontrataVisibleAsync</c> (lectura) como
/// puerta, y este ratchet no lo detecta: eso fue exactamente lo que le pasó a
/// <c>ObtenerCredencialAccesoEmpresaQuery</c> — un usuario de portal (rol
/// Cliente) leía en claro la contraseña de una contratista de su propio
/// Cliente porque esa cartera de lectura la incluye. El ratchet protege de
/// una sesión privilegiada de plataforma y de un Id fuera de cualquier
/// cartera; no protege de que la cartera elegida sea la equivocada.
/// </para>
/// </summary>
public class ConsultasDeSecretosMarcadasTests
{
    /// <summary>
    /// Propiedades que solo existen descifradas: son las que el
    /// <c>CaeManagerDbContext</c> convierte con un protector de Data
    /// Protection. Si se añade otro secreto cifrado en reposo, se añade aquí.
    /// </summary>
    private static readonly Regex PatronLecturaDeSecreto = new(
        @"\.Contrasena\b|\.TokenAcceso\b|\.RefreshToken\b|\.ClientState\b",
        RegexOptions.Compiled);

    [Fact]
    public void Toda_Query_que_devuelve_credenciales_esta_marcada_como_consulta_de_secretos()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");

        // El DTO de una credencial se reconoce por lo que expone, no por cómo
        // se llame: si tiene a la vez Usuario y Contrasena, es una credencial.
        var sinMarcar = application.GetTypes()
            .Where(t => t.Name.EndsWith("Query", StringComparison.Ordinal))
            .Where(t => !typeof(IConsultaDeSecretosDeTenant).IsAssignableFrom(t))
            .Where(DevuelveUnDtoConContrasena)
            .Select(t => t.FullName!)
            .OrderBy(x => x)
            .ToList();

        string.Join("\n", sinMarcar).Should().BeEmpty(
            "una Query que devuelve una contraseña descifrada tiene que implementar IConsultaDeSecretosDeTenant; " +
            "si no, una sesión privilegiada de plataforma se la lleva junto con el resto del tenant");
    }

    [Fact]
    public void Ninguna_Query_lee_un_secreto_cifrado_sin_estar_en_la_lista_conocida()
    {
        // Red de seguridad del test de arriba: cubre el caso en el que la
        // credencial no viaje en un DTO con forma reconocible (se aplane en un
        // string, se meta en un diccionario, se concatene). Lista corta y
        // revisada a mano; añadir una entrada es una decisión de diseño que se
        // revisa en el mismo commit.
        var conocidas = new HashSet<string>
        {
            "src/CaeManager.Application/Empresas/Queries/ObtenerCredencialAccesoEmpresa/ObtenerCredencialAccesoEmpresaQuery.cs",
            "src/CaeManager.Application/Subcontratas/Queries/ObtenerCredencialAccesoSubcontrata/ObtenerCredencialAccesoSubcontrataQuery.cs",
        };

        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src", "CaeManager.Application");

        var infractores = Directory
            .EnumerateFiles(directorio, "*Query.cs", SearchOption.AllDirectories)
            .Select(archivo => (Ruta: Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/'), archivo))
            .Where(x => !conocidas.Contains(x.Ruta))
            .Where(x => File.ReadLines(x.archivo).Any(linea => PatronLecturaDeSecreto.IsMatch(linea)))
            .Select(x => x.Ruta)
            .OrderBy(x => x)
            .ToList();

        string.Join("\n", infractores).Should().BeEmpty(
            "estas Queries leen un valor que el DbContext descifra; o no deben proyectarlo, o deben implementar " +
            "IConsultaDeSecretosDeTenant y entrar en la lista de este test explicando por qué");

        // Guarda del propio test: si la lista dejara de corresponderse con el
        // código, estaría vigilando archivos que ya no existen.
        conocidas.Should().OnlyContain(ruta => File.Exists(Path.Combine(raiz, ruta.Replace('/', Path.DirectorySeparatorChar))));
    }


    /// <summary>
    /// Una consulta de secretos tambien tiene que acotar por CARTERA, no solo
    /// estar marcada.
    ///
    /// <para>
    /// El marcador <see cref="IConsultaDeSecretosDeTenant"/> y su behavior
    /// resuelven una cosa muy concreta: que una sesion privilegiada de
    /// plataforma no se lleve el secreto. No dicen nada del usuario normal del
    /// tenant, y las dos consultas de credenciales que existen filtraban solo
    /// por el Id del agregado.
    /// </para>
    ///
    /// <para>
    /// Es exactamente el fallo del Issue #18 --"un Gestor CAE podia leer
    /// cualquier fila fuera de su cartera con solo conocer el Guid"-- sobre las
    /// filas mas sensibles del tenant: la URL, el usuario y la contrasena de
    /// acceso al portal del cliente. Se descubrio auditando por mutacion el
    /// ratchet de escritura, que no mira Queries; sin esta regla, el camino de
    /// lectura se quedaba sin vigilancia de ningun tipo.
    /// </para>
    /// </summary>
    [Fact]
    public void Toda_consulta_de_secretos_acota_por_cartera()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");
        var tipos = application.GetTypes();

        var consultas = tipos
            .Where(t => typeof(IConsultaDeSecretosDeTenant).IsAssignableFrom(t) && !t.IsInterface)
            .ToList();

        var sinAlcance = consultas
            .Where(consulta => tipos
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .Where(t => t.GetInterfaces().Any(i =>
                    i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(MediatR.IRequestHandler<,>)
                    && i.GetGenericArguments()[0] == consulta))
                .Any(handler => !handler.GetConstructors()
                    .SelectMany(c => c.GetParameters())
                    .Any(p => p.ParameterType == typeof(IAlcanceDatosService))))
            .Select(t => t.Name)
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, sinAlcance).Should().BeEmpty(
            "una consulta que devuelve una credencial descifrada tiene que comprobar ademas que el agregado esta " +
            "en la cartera del usuario; estar marcada como IConsultaDeSecretosDeTenant solo la protege de una " +
            "sesion privilegiada de plataforma, no de un Gestor CAE que conozca el Id");
    }

    /// <summary>Guarda: si no hubiera ninguna consulta marcada, el test anterior pasaria en vacio.</summary>
    [Fact]
    public void Hay_consultas_de_secretos_que_inspeccionar()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");

        application.GetTypes()
            .Count(t => typeof(IConsultaDeSecretosDeTenant).IsAssignableFrom(t) && !t.IsInterface)
            .Should().BeGreaterThan(0, "sin consultas marcadas, la regla de arriba no observa nada");
    }
    private static bool DevuelveUnDtoConContrasena(Type query)
    {
        var respuesta = query.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(MediatR.IRequest<>))
            .Select(i => i.GetGenericArguments()[0])
            .FirstOrDefault();

        if (respuesta is null) return false;

        // El DTO puede venir suelto o dentro de una colección.
        var candidatos = respuesta.IsGenericType
            ? new[] { respuesta }.Concat(respuesta.GetGenericArguments())
            : [respuesta];

        return candidatos.Any(t =>
            t.GetProperty("Contrasena") is not null && t.GetProperty("Usuario") is not null);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
