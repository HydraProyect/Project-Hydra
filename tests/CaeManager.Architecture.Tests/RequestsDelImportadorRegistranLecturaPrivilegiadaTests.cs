using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Issue #2001: toda lectura que el Importador hace bajo una Sesión Privilegiada deja una fila en
/// <c>RegistrosActividadSoporte</c>. Lo hace <c>RegistroLecturaSesionPrivilegiadaBehavior</c> para
/// los requests marcados <see cref="ILecturaRegistradaEnSesionPrivilegiada"/>: este test fija que
/// todo request del Importador lo esté, salvo el que solo escribe el historial, de modo que un
/// request nuevo en <c>CaeManager.Application.Importacion</c> no nazca leyendo sin rastro.
///
/// Y fija, sin base de datos, el texto de la migración que una revisión podría romper sin ponerla
/// en rojo: la suite de integración (<c>RegistroLecturaSesionPrivilegiadaBajoRuntimeTests</c>)
/// mide el comportamiento en CI.
/// </summary>
public class RequestsDelImportadorRegistranLecturaPrivilegiadaTests
{
    /// <summary>
    /// Solo escribe el historial de la carga ya hecha: no lee datos del Tenant. Su escritura bajo
    /// Aprovisionamiento la cubre la auditoría de escrituras, no este registro de lecturas.
    /// </summary>
    private static readonly HashSet<string> Excluidos = ["RegistrarHistorialImportacionCommand"];

    [Fact]
    public void Todo_request_del_Importador_que_lee_registra_la_lectura_en_Sesion_Privilegiada()
    {
        var requests = typeof(ILecturaRegistradaEnSesionPrivilegiada).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Namespace is { } ns
                        && (ns == "CaeManager.Application.Importacion" || ns.StartsWith("CaeManager.Application.Importacion.", StringComparison.Ordinal))
                        && typeof(IBaseRequest).IsAssignableFrom(t))
            .ToList();

        requests.Should().HaveCountGreaterThan(5, "el instrumento tiene que ver los requests del Importador");
        requests.Select(t => t.Name).Should().Contain(["EjecutarImportacionCommand", "EjecutarImportacionCombinadaCommand",
            "AnalizarPlantillaCombinadaQuery", "ObtenerHistorialImportacionesQuery"]);

        requests
            .Where(t => !Excluidos.Contains(t.Name))
            .Where(t => !typeof(ILecturaRegistradaEnSesionPrivilegiada).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .Should().BeEmpty("una lectura del Importador bajo Sesión Privilegiada sin fila es el hueco que cerró la issue #2001");

        requests.Where(t => Excluidos.Contains(t.Name)).Should().HaveCount(Excluidos.Count,
            "una exclusión que ya no existe se retira de la lista");
    }

    /// <summary>
    /// Las páginas del Importador. Lo que envían es lo que el Actor de Plataforma TALVEG lee al
    /// usarlo, venga del espacio de nombres que venga (p. ej. <c>ObtenerNombreCuentaQuery</c>).
    /// </summary>
    private static readonly string[] PaginasDelImportador =
    [
        "src/CaeManager.Web/Features/Importacion",
        "src/CaeManager.Web/Features/Clientes/Pages/ImportarClientes.razor",
        "src/CaeManager.Web/Features/Clientes/Pages/ImportarCombinado.razor",
        "src/CaeManager.Web/Features/Documentos/Pages/ImportarDocumentos.razor",
    ];

    /// <summary>
    /// En un <c>.razor</c> (sin comentarios, ver <see cref="LimpiadorDeComentarios"/>), cualquier
    /// llamada a <c>Send</c> tolerando espacios y genéricos, y la forma literal <c>.Send(new X(</c>
    /// anclada a donde empieza esa llamada.
    /// </summary>
    private static readonly Regex CualquierEnvioRazor = new(@"\.\s*@?Send\s*(<[^>(]*>)?\s*\(", RegexOptions.Compiled);
    private static readonly Regex EnvioLiteralRazor = new(@"\G\.\s*@?Send\s*\(\s*new\s+(?<tipo>[A-Za-z_][\w.]*)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Los envíos de un fuente de página: <c>(tipo, null)</c> si el argumento es <c>new X(...)</c> y
    /// <c>(null, posición)</c> si es cualquier otra cosa, que este instrumento no sabe nombrar. Los
    /// <c>.razor.cs</c> se recorren con Roslyn (InvocationExpression cuyo nombre es <c>Send</c>), así que
    /// los espacios, los saltos y los comentarios entre el punto, el nombre y el paréntesis no lo ciegan.
    /// </summary>
    internal static IEnumerable<(string? Tipo, string? Opaco)> Envios(string ruta, string texto)
    {
        var nombre = Path.GetFileName(ruta);
        if (ruta.EndsWith(".razor.cs", StringComparison.Ordinal) || ruta.EndsWith(".cs", StringComparison.Ordinal))
        {
            var raiz = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(texto).GetRoot();
            foreach (var llamada in raiz.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            {
                var metodo = llamada.Expression switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                    Microsoft.CodeAnalysis.CSharp.Syntax.MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
                    _ => null,
                };
                if (metodo != "Send")
                    continue;

                var linea = llamada.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var primero = llamada.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                if (primero is Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax creacion)
                    yield return (creacion.Type.ToString().Split('.')[^1].Trim(), null);
                else
                    yield return (null, $"{nombre}:{linea}");
            }
            yield break;
        }

        var limpio = LimpiadorDeComentarios.Quitar(texto, razor: true);
        foreach (Match envio in CualquierEnvioRazor.Matches(limpio))
        {
            var literal = EnvioLiteralRazor.Match(limpio, envio.Index);
            var linea = limpio[..envio.Index].Count(c => c == '\n') + 1;
            yield return literal.Success
                ? (literal.Groups["tipo"].Value.Split('.')[^1], null)
                : (null, $"{nombre}:{linea}");
        }
    }

    [Fact]
    public void Todo_request_que_envia_la_pagina_del_Importador_registra_la_lectura_en_Sesion_Privilegiada()
    {
        var raiz = BuscarRaizDelRepositorio();
        var ficheros = PaginasDelImportador
            .Select(r => Path.Combine(raiz, r.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(r => Directory.Exists(r)
                ? Directory.EnumerateFiles(r, "*.razor*", SearchOption.AllDirectories)
                : File.Exists(r) ? [r] : [])
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".razor.cs", StringComparison.Ordinal))
            .ToList();
        ficheros.Should().Contain(f => f.EndsWith("Importacion.razor.cs", StringComparison.Ordinal),
            "el instrumento tiene que leer la página del Importador");

        var envios = ficheros.SelectMany(f => Envios(f, File.ReadAllText(f))).ToList();

        // Un envío que no es «.Send(new X(...))» (una variable, un método, new() sin tipo) no lo puede
        // nombrar este instrumento: se exige la forma literal para no dar por marcado lo que no ve.
        envios.Where(e => e.Opaco is not null).Select(e => e.Opaco).Should().BeEmpty(
            "toda llamada a Send en una página del Importador tiene que ser «.Send(new Request(...))» para que " +
            "este test pueda comprobar que el request lleva ILecturaRegistradaEnSesionPrivilegiada (issue #2001); " +
            "si hace falta otra forma, amplía el instrumento antes");

        var enviados = envios.Where(e => e.Tipo is not null).Select(e => e.Tipo!).Distinct().ToList();
        enviados.Should().Contain(["ObtenerHistorialImportacionesQuery", "ObtenerNombreCuentaQuery", "EjecutarImportacionCommand"],
            "el instrumento tiene que reconocer los envíos de la página");

        var requestsPorNombre = typeof(ILecturaRegistradaEnSesionPrivilegiada).Assembly.GetTypes()
            .Where(t => typeof(IBaseRequest).IsAssignableFrom(t))
            .ToLookup(t => t.Name);

        enviados.Where(n => !requestsPorNombre.Contains(n)).Should().BeEmpty(
            "cada request enviado tiene que encontrarse en Application para poder comprobarlo");
        enviados
            .Where(n => !Excluidos.Contains(n))
            .Where(n => !requestsPorNombre[n].All(t => typeof(ILecturaRegistradaEnSesionPrivilegiada).IsAssignableFrom(t)))
            .Should().BeEmpty("lo que la página del Importador lee bajo Sesión Privilegiada deja fila (issue #2001)");
    }

    /// <summary>Sensibilidad del detector: las formas que la versión con expresión regular no veía.</summary>
    [Theory]
    [InlineData("class P { async Task F() { await Mediator. Send(consulta); } }")]
    [InlineData("class P { async Task F() { await Mediator.Send /*c*/ (consulta); } }")]
    [InlineData("class P { async Task F() { await Mediator\n    .Send(\n        Construir()); } }")]
    [InlineData("class P { async Task F() { await Mediator.Send<int>(consulta); } }")]
    [InlineData("class P { async Task F() { await Mediator.Send(new(1)); } }")]
    [InlineData("class P { async Task F() { await Mediator?.Send(consulta); } }")]
    [InlineData("class P { async Task F() { await Mediator.@Send(consulta); } }")]
    public void El_detector_ve_como_opaco_un_envio_que_no_es_new_X_en_cualquier_forma(string fuente)
    {
        Envios("Pagina.razor.cs", fuente).Should().ContainSingle().Which.Opaco.Should().NotBeNull();
    }

    [Theory]
    [InlineData("class P { async Task F() { await Mediator . Send ( new ObtenerHistorialImportacionesQuery ( ) ); } }")]
    [InlineData("class P { async Task F() { await Mediator.Send(/*c*/ new Ns.ObtenerHistorialImportacionesQuery()); } }")]
    public void El_detector_nombra_el_request_de_un_envio_new_X_con_espacios_y_comentarios(string fuente)
    {
        Envios("Pagina.razor.cs", fuente).Should().ContainSingle().Which.Tipo.Should().Be("ObtenerHistorialImportacionesQuery");
    }

    [Theory]
    [InlineData("@code { async Task F() { await Mediator. Send(consulta); } }", null)]
    [InlineData("@code { async Task F() { await Mediator.Send /*c*/ (consulta); } }", null)]
    [InlineData("@code { async Task F() { await Mediator . Send ( new ObtenerHistorialImportacionesQuery() ); } }", "ObtenerHistorialImportacionesQuery")]
    [InlineData("@code { async Task F() { await Mediator.@Send(consulta); } }", null)]
    [InlineData("@code { async Task F() { await Mediator.@Send(new ObtenerHistorialImportacionesQuery()); } }", "ObtenerHistorialImportacionesQuery")]
    public void En_un_razor_el_detector_tolera_espacios_y_comentarios(string fuente, string? tipo)
    {
        var envio = Envios("Pagina.razor", fuente).Should().ContainSingle().Subject;
        envio.Tipo.Should().Be(tipo);
        (envio.Opaco is null).Should().Be(tipo is not null);
    }

    private static string BuscarRaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }

    private static string Up => new RegistraActividadSoportePorSesionPrivilegiada()
        .UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Down => new RegistraActividadSoportePorSesionPrivilegiada()
        .DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    [Fact]
    public void La_funcion_es_SECURITY_DEFINER_con_search_path_fijo_y_valida_la_sesion_dentro()
    {
        string[] obligatorias =
        [
            "SECURITY DEFINER",
            "SET search_path TO 'pg_catalog', 'pg_temp'",
            "public.app_ctx_valido()",
            "s.\"Id\" = p_sesion",
            "s.\"TenantObjetivoId\" = v_tenant",
            "s.\"CerradaEnUtc\" IS NULL",
            "s.\"InicioEnUtc\" <= v_ahora",
            "s.\"ExpiraEnUtc\" > v_ahora",
            "c.\"UsuarioPlataformaId\" = v_actor",
            "c.\"Estado\" = 'Vigente'",
            "c.\"VigenciaDesde\" <= v_ahora",
            "(c.\"VigenciaHasta\" IS NULL OR c.\"VigenciaHasta\" > v_ahora)",
            "c.\"EsAlcanceGlobal\" OR EXISTS",
            "IF NOT FOUND THEN",
            "RETURN 'sesion_no_autorizada';",
        ];
        foreach (var construccion in obligatorias)
            IndiceEnCodigo(Up, construccion).Should().BeGreaterThan(-1,
                "la función tiene que contener «{0}» como código, no en un comentario ni dentro de un literal", construccion);

        IndiceEnCodigo(Up, "RETURN 'sesion_no_autorizada';").Should().BeLessThan(IndiceEnCodigo(Up, "INSERT INTO"),
            "la sesión se valida antes de insertar");
        IndiceEnCodigo(Up, "LOCK TABLE").Should().Be(-1, "la función no bloquea tablas");
        IndiceEnCodigo(Up, "lock_timeout").Should().Be(-1);
    }

    [Fact]
    public void La_hora_es_la_del_reloj_tomada_al_validar_y_no_la_del_inicio_de_la_transaccion()
    {
        // now() es la hora de inicio de la transacción: una transacción abierta antes de que la
        // sesión o la concesión caducaran seguiría viéndolas vigentes. La hora se toma justo antes
        // de validar la sesión, sin nada que pueda esperar entre medias, y fecha la fila.
        var asignacion = IndiceEnCodigo(Up, "v_ahora := clock_timestamp();");
        asignacion.Should().BeGreaterThan(IndiceEnCodigo(Up, "RETURN 'valor_no_valido';"),
            "la hora se toma tras las comprobaciones previas, en el punto de validar");
        asignacion.Should().BeLessThan(IndiceEnCodigo(Up, "FROM public.\"SesionesPrivilegiadas\" s"),
            "la hora se toma antes de buscar la sesión nombrada");
        CoincidenciasEnCodigo(new Regex(@"v_ahora\s*(:=|=|timestamptz\s*:=|timestamptz\s+DEFAULT)"), Up)
            .Should().ContainSingle("v_ahora se asigna una sola vez");
        IndiceEnCodigo(Up, "now()").Should().Be(-1);
        IndiceEnCodigo(Up, "p_detalle, v_ahora,").Should().BeGreaterThan(-1,
            "la fila se fecha con la misma hora con la que se comprobó la sesión");
    }

    private static readonly Regex ListaDeDetalles = new(@"p_detalle NOT IN \((?<lista>[^)]*)\)", RegexOptions.Compiled);

    /// <summary>
    /// La función de la migración VIGENTE (la última que la crea o la reemplaza) solo admite como
    /// detalle los nombres de los requests marcados, ni uno más ni uno menos: marcar un request
    /// nuevo obliga a una migración nueva que amplíe la lista, y una lista con un nombre que ya no
    /// es un request marcado se nota aquí.
    /// </summary>
    [Fact]
    public void La_lista_cerrada_de_detalles_de_la_migracion_vigente_es_exactamente_la_de_los_requests_marcados()
    {
        var migraciones = typeof(RegistraActividadSoportePorSesionPrivilegiada).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Id: t.GetCustomAttribute<MigrationAttribute>()?.Id, Tipo: t))
            .Where(m => m.Id is not null)
            .Select(m => (Id: m.Id!, Sql: string.Join("\n", ((Migration)Activator.CreateInstance(m.Tipo)!)
                .UpOperations.OfType<SqlOperation>().Select(o => o.Sql))))
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        var vigente = DefinicionVigente(migraciones);
        var coincidencias = CoincidenciasEnCodigo(ListaDeDetalles, vigente.Sql);
        coincidencias.Should().ContainSingle("la función de {0} tiene que filtrar el detalle por lista cerrada", vigente.Id);
        var admitidos = coincidencias[0].Groups["lista"].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim('\''))
            .ToList();

        var marcados = typeof(ILecturaRegistradaEnSesionPrivilegiada).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ILecturaRegistradaEnSesionPrivilegiada).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        marcados.Should().HaveCount(11, "el instrumento tiene que ver los requests marcados");
        admitidos.Should().OnlyHaveUniqueItems();
        admitidos.Should().BeEquivalentTo(marcados,
            "el detalle de la función {0} es una lista cerrada con el nombre exacto de cada request marcado", vigente.Id);
    }

    private static readonly Regex DefinicionDeLaFuncion = new(
        @"CREATE\s+(OR\s+REPLACE\s+)?FUNCTION\s+public\.app_registrar_actividad_soporte_por_sesion\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EliminacionDeLaFuncion = new(
        @"DROP\s+FUNCTION\s+(IF\s+EXISTS\s+)?public\.app_registrar_actividad_soporte_por_sesion\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// La definición vigente: la ÚLTIMA <c>CREATE [OR REPLACE] FUNCTION</c> de la última migración que
    /// la define, desde la cabecera hasta el cierre de su cuerpo (<c>$$;</c>). Una migración que solo
    /// toque GRANT o DROP no cuenta como definición, y una migración posterior que la elimine sin
    /// volver a crearla hace fallar el test (la lista ya no protegería nada).
    /// </summary>
    internal static (string Id, string Sql) DefinicionVigente(IReadOnlyList<(string Id, string Sql)> migraciones)
    {
        var definidoras = migraciones.Where(m => DefinicionDeLaFuncion.IsMatch(m.Sql)).ToList();
        definidoras.Should().NotBeEmpty("alguna migración define la función");
        var ultima = definidoras[^1];

        var posteriores = migraciones.Where(m => string.CompareOrdinal(m.Id, ultima.Id) > 0)
            .Where(m => EliminacionDeLaFuncion.IsMatch(m.Sql))
            .Select(m => m.Id)
            .ToList();
        posteriores.Should().BeEmpty("una migración posterior a {0} elimina la función sin redefinirla", ultima.Id);

        // Dentro de la propia migración, un DROP después de la última definición también la elimina.
        var definicion = DefinicionDeLaFuncion.Matches(ultima.Sql)[^1];
        EliminacionDeLaFuncion.Matches(ultima.Sql).Where(d => d.Index > definicion.Index)
            .Should().BeEmpty("{0} elimina la función después de definirla", ultima.Id);

        var cuerpo = ultima.Sql.IndexOf("$$", definicion.Index, StringComparison.Ordinal);
        cuerpo.Should().BeGreaterThan(-1);
        var cierre = ultima.Sql.IndexOf("$$;", cuerpo + 2, StringComparison.Ordinal);
        cierre.Should().BeGreaterThan(cuerpo, "la definición cierra su cuerpo");
        return (ultima.Id, SinComentariosSql(ultima.Sql[definicion.Index..(cierre + 3)]));
    }

    /// <summary>
    /// Quita los comentarios SQL (<c>--</c> hasta fin de línea y <c>/* */</c>, que en PostgreSQL se
    /// anidan) respetando los literales entre comillas simples (<c>'…'</c> con <c>''</c> como comilla
    /// escapada, y <c>E'…'</c> con escapes de barra invertida): una lista que solo aparece en un
    /// comentario no es la lista que la función aplica.
    /// </summary>
    internal static string SinComentariosSql(string sql) => Escanear(sql).Texto;

    /// <summary>
    /// El SQL sin comentarios y, para cada posición de ese texto, si cae dentro de un literal de
    /// cadena (comillas incluidas). El cuerpo <c>$$ … $$</c> de la función no se trata como literal:
    /// es el código que se busca.
    /// </summary>
    internal static (string Texto, bool[] EnLiteral) Escanear(string sql)
    {
        var salida = new System.Text.StringBuilder(sql.Length);
        var enLiteral = new List<bool>(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var esE = (sql[i] is 'E' or 'e') && i + 1 < sql.Length && sql[i + 1] == '\''
                      && (i == 0 || !(char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] == '_'));
            if (sql[i] == '\'' || esE)
            {
                var inicio = i;
                var fin = i + (esE ? 2 : 1);
                while (fin < sql.Length)
                {
                    if (esE && sql[fin] == '\\') { fin += 2; continue; }
                    if (sql[fin] == '\'')
                    {
                        if (fin + 1 < sql.Length && sql[fin + 1] == '\'') { fin += 2; continue; }
                        break;
                    }
                    fin++;
                }
                fin = Math.Min(fin + 1, sql.Length);
                salida.Append(sql, inicio, fin - inicio);
                for (var k = inicio; k < fin; k++) enLiteral.Add(true);
                i = fin;
            }
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
            }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var profundidad = 1;
                i += 2;
                while (i < sql.Length && profundidad > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { profundidad++; i += 2; }
                    else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { profundidad--; i += 2; }
                    else i++;
                }
                salida.Append(' ');
                enLiteral.Add(false);
            }
            else
            {
                salida.Append(sql[i]);
                enLiteral.Add(false);
                i++;
            }
        }

        return (salida.ToString(), enLiteral.ToArray());
    }

    /// <summary>
    /// Las coincidencias del patrón en el SQL sin comentarios que empiezan fuera de un literal: un
    /// <c>PERFORM 'p_detalle NOT IN (...)'</c> no es una comprobación que la función aplique.
    /// </summary>
    internal static IReadOnlyList<Match> CoincidenciasEnCodigo(Regex patron, string sql)
    {
        var (texto, enLiteral) = Escanear(sql);
        return patron.Matches(texto).Where(m => !enLiteral[m.Index]).ToList();
    }

    /// <summary>
    /// Posición (en el SQL sin comentarios) de la primera aparición de la construcción que empieza
    /// fuera de un literal; -1 si solo aparece en comentarios o dentro de literales.
    /// </summary>
    internal static int IndiceEnCodigo(string sql, string construccion) =>
        CoincidenciasEnCodigo(new Regex(Regex.Escape(construccion)), sql) is [var primera, ..] ? primera.Index : -1;

    [Fact]
    public void La_definicion_vigente_es_la_ultima_CREATE_de_la_ultima_migracion_que_la_define()
    {
        const string vieja = "CREATE FUNCTION public.app_registrar_actividad_soporte_por_sesion(p uuid) AS $$ BEGIN IF p_detalle NOT IN ('Viejo') THEN END IF; END; $$;";
        const string nueva = "CREATE OR REPLACE FUNCTION public.app_registrar_actividad_soporte_por_sesion(p uuid) AS $$ BEGIN IF p_detalle NOT IN ('Nuevo') THEN END IF; END; $$;";
        const string grant = "GRANT EXECUTE ON FUNCTION public.app_registrar_actividad_soporte_por_sesion(uuid) TO cae_app_soporte; -- p_detalle NOT IN ('Engano')";

        // Dos definiciones en la misma migración: cuenta la última, y el texto de fuera no entra.
        var dos = DefinicionVigente([("1", vieja + "\n" + nueva + "\nSELECT 'p_detalle NOT IN (''Fuera'')';")]);
        CoincidenciasEnCodigo(ListaDeDetalles, dos.Sql).Should().ContainSingle().Which.Groups["lista"].Value.Should().Be("'Nuevo'");

        const string cabecera = "CREATE FUNCTION public.app_registrar_actividad_soporte_por_sesion(p uuid) AS $$ BEGIN ";
        IReadOnlyList<Match> Lista(string cuerpo) =>
            CoincidenciasEnCodigo(ListaDeDetalles, DefinicionVigente([("1", cabecera + cuerpo + " END; $$;")]).Sql);

        // Una lista que solo aparece en un comentario del cuerpo no es la lista que se aplica.
        Lista("\n  -- IF p_detalle NOT IN ('Comentada') THEN\n  RETURN 'x';\n").Should().BeEmpty("la lista solo está en un comentario --");
        Lista("/* p_detalle NOT IN ('Comentada') */ RETURN 'x';").Should().BeEmpty("la lista solo está en un comentario /* */");

        // Los comentarios de bloque se anidan: el primer «*/» no cierra el exterior.
        Lista("/* a /* b */ p_detalle NOT IN ('X') */ RETURN 'x';").Should().BeEmpty("la lista está dentro de un comentario anidado");

        // Una lista dentro de un literal no es una comprobación que se aplique.
        Lista("PERFORM 'p_detalle NOT IN (''X'')';").Should().BeEmpty("la lista solo está dentro de un literal");
        Lista("PERFORM E'\\' p_detalle NOT IN (''X'')';").Should().BeEmpty("la lista solo está dentro de un literal E'…'");

        // Un literal con «--» o «/*» dentro no se confunde con un comentario.
        Lista("IF x = 'a--b/*c''' OR p_detalle NOT IN ('Real') THEN END IF;").Should().ContainSingle()
            .Which.Groups["lista"].Value.Should().Be("'Real'");

        // Lo mismo para las construcciones obligatorias.
        IndiceEnCodigo("PERFORM 'IF NOT FOUND THEN'; /* IF NOT FOUND THEN */ -- IF NOT FOUND THEN", "IF NOT FOUND THEN")
            .Should().Be(-1);
        IndiceEnCodigo("x := '--'; IF NOT FOUND THEN", "IF NOT FOUND THEN").Should().BeGreaterThan(-1);

        // Una migración posterior que solo toca GRANT no es una definición.
        DefinicionVigente([("1", nueva), ("2", grant)]).Id.Should().Be("1");

        // Una posterior que la elimina hace fallar.
        var eliminada = () => DefinicionVigente([("1", nueva), ("2", "DROP FUNCTION public.app_registrar_actividad_soporte_por_sesion(uuid);")]);
        eliminada.Should().Throw<Exception>();

        // Y un DROP tras la última definición dentro de la misma migración, también.
        var mismaMigracion = () => DefinicionVigente([("1", nueva + "\nDROP FUNCTION IF EXISTS public.app_registrar_actividad_soporte_por_sesion;")]);
        mismaMigracion.Should().Throw<Exception>();
    }

    [Fact]
    public void La_funcion_solo_registra_en_READ_COMMITTED()
    {
        // Bajo REPEATABLE READ o SERIALIZABLE la función vería una instantánea anterior al cierre de
        // la sesión o a la revocación de la concesión.
        var comprobacion = IndiceEnCodigo(Up, "IF current_setting('transaction_isolation') <> 'read committed' THEN");
        var rechazo = IndiceEnCodigo(Up, "RETURN 'aislamiento_no_admitido';");
        comprobacion.Should().BeGreaterThan(-1);
        rechazo.Should().BeGreaterThan(comprobacion);
        rechazo.Should().BeLessThan(IndiceEnCodigo(Up, "INSERT INTO"), "el rechazo va antes de insertar");
        Up.Should().NotContain("left(p_detalle").And.NotContain("p_detalle !~",
            "el detalle no se recorta ni se acepta por patrón: o es un nombre de la lista o no se inserta");
    }

    [Fact]
    public void EXECUTE_solo_para_cae_app_soporte_sin_tocar_los_GRANT_de_la_tabla_y_Down_simetrico()
    {
        const string firma = "public.app_registrar_actividad_soporte_por_sesion(uuid, uuid, text, text)";

        Up.Should().Contain($"REVOKE ALL ON FUNCTION {firma} FROM PUBLIC;")
            .And.Contain($"GRANT EXECUTE ON FUNCTION {firma} TO cae_app_soporte;");
        Up.Split("GRANT ").Length.Should().Be(2, "un único GRANT: el EXECUTE para cae_app_soporte");
        Up.Should().NotContain("ON TABLE", "la conexión de soporte no gana privilegios sobre la tabla");
        Down.Trim().Should().Be($"DROP FUNCTION {firma};");
    }
}
