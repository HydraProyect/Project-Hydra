using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Trinquete de acceso crudo a las Asignaciones</b> (paso M3' del ADR-013, § 4.6, condiciones 2
/// y 3; decisión M-4): Asignación de Operación y Asignación de Cartera son una sola tabla cada una en
/// el Núcleo, compartida por Outbound e Inbound, y el problema no es la tabla sino leerla sin decir de
/// qué servicio. #2215 y #2223 fueron eso: un filtro de servicio que cada consulta tenía que recordar.
///
/// <para>
/// <b>Qué observa.</b> Cada acceso a <c>AsignacionesOperacion</c> o <c>AsignacionesCartera</c> en el
/// C# de <c>src</c> (sin migraciones) y en el SQL vigente de las migraciones (funciones y políticas),
/// con la mecánica de <see cref="AccesosAAsignaciones"/>. Un acceso sin filtro de Servicio es
/// <b>crudo</b>; los crudos de hoy están en <c>Congelados/Asignaciones-acceso-crudo-sin-servicio.txt</c>,
/// una línea por fichero (u objeto SQL), miembro y tabla, con su recuento. Un crudo nuevo es
/// <c>NUEVA</c> o <c>CRECE</c>; uno que pasa a filtrar por Servicio o desaparece es <c>BAJA</c> u
/// <c>OBSOLETA</c> y obliga a bajar la lista en el mismo commit. La lista solo baja.
/// </para>
///
/// <para>
/// <b>Qué no es.</b> No sustituye a <see cref="AccesoRestringidoACatalogosDeAsignacionTests"/>, que
/// sigue decidiendo qué ficheros pueden tocar las tablas (filtro de posición); este decide si cada
/// acceso dice de qué servicio. Tampoco es el puerto por servicio de la condición 1: cuando exista,
/// sus consultas filtran por Servicio y no entran en la lista, y los consumidores que migren a él
/// bajan la lista.
/// </para>
///
/// <para>
/// <b>Lo que NO detecta</b> (huecos declarados):
/// </para>
/// <list type="bullet">
/// <item>Que la comparación de Servicio restrinja de verdad: una disyunción
/// (<c>x || o.Servicio != Outbound</c>) o una proyección (<c>Select(o =&gt; o.Servicio == s)</c>)
/// cuentan como filtro. El instrumento ve que la sentencia nombra el servicio, no el valor ni la
/// semántica.</item>
/// <item>Un filtro aplicado en otra sentencia (<c>var q = ctx.AsignacionesOperacion; q = q.Where(…)</c>)
/// ni un predicado de servicio de otro tipo referenciado sin cualificar: cuentan como crudos
/// (falso positivo, va a la lista).</item>
/// <item>Accesos sin nombrar la tabla ni la entidad: <c>ctx.Add(entidad)</c>, <c>Entry(…)</c>,
/// <c>Set(typeof(…))</c>, <c>Model.FindEntityType</c>, reflexión.</item>
/// <item>En SQL: una función o política creada dentro de un <c>DO $$ … EXECUTE … $$</c> o reescrita por
/// <c>ReescrituraPoliticasContextoRls</c> en ejecución; sobrecargas de una función con el mismo nombre
/// (se queda la última definición); el SQL de datos de una sola vez en <c>Up</c>; y la sentencia se
/// corta por <c>;</c>, así que una subconsulta sin servicio dentro de una sentencia que compara
/// <c>"Servicio"</c> en otro sitio cuenta como filtrada.</item>
/// <item>Una redefinición de un objeto SQL ya listado hereda su línea: solo se nota si cambia el
/// recuento de apariciones crudas.</item>
/// <item>Literales SQL construidos por concatenación o con la tabla en un hueco de interpolación; alias
/// <c>global using</c> de las entidades; <c>Find&lt;T&gt;</c> y <c>Entry&lt;T&gt;</c> genéricos.</item>
/// <item>Las sobrecargas de un mismo miembro se suman bajo <c>Tipo.Miembro</c>: un crudo nuevo en una
/// sobrecarga es <c>CRECE</c>, no <c>NUEVA</c>.</item>
/// <item>La cadena de la consulta se ensancha a la consulta exterior cuando el acceso está dentro de
/// una lambda pasada como argumento o de una expresión de consulta: una subconsulta interior sin
/// servicio comparte el filtro de la exterior. Una lambda con cuerpo de bloque no ensancha: sus
/// sentencias son independientes.</item>
/// <item>En SQL, el filtro es un <c>"Servicio"</c> seguido de <c>=</c>, <c>&lt;&gt;</c>, <c>!=</c>,
/// <c>IN</c> o <c>IS</c> que no sea la primera asignación tras <c>SET</c>: una segunda asignación
/// (<c>SET "Estado" = …, "Servicio" = …</c>) cuenta como filtro, y la forma desparsada de
/// <c>pg_dump</c> (<c>("Servicio")::text = …</c>) no casa. La función SQL con cuerpo <c>AS '…'</c> o
/// <c>BEGIN ATOMIC</c> no se separa de la siguiente de la misma migración (hoy todas usan <c>$$</c>).</item>
/// <item>El SQL de <c>tests/</c> y de los guiones de <c>scripts/</c>.</item>
/// </list>
/// </summary>
public class AccesoCrudoAAsignacionesSinServicioTests
{
    private const string Lista = "Asignaciones-acceso-crudo-sin-servicio";

    private const string Guia =
        "Asignación de Operación y Asignación de Cartera son una sola tabla en el Núcleo para Outbound e Inbound " +
        "(ADR-013 § 4.6, M-4): toda consulta dice de qué servicio. NUEVA o CRECE: no añadas la línea; filtra por " +
        "o.Servicio (en la cartera, por el Servicio de su Asignación de Operación) en la misma sentencia, usa un " +
        "predicado de servicio con nombre o el puerto por servicio cuando exista. BAJA u OBSOLETA: has retirado un " +
        "acceso crudo; baja o borra su línea en tests/CaeManager.Architecture.Tests/Congelados/" + Lista + ".txt " +
        "en el mismo commit. Si un defecto de servicio compartido obliga a añadir una línea, va con su motivo en la PR.";

    [Fact]
    public void Ningun_acceso_nuevo_a_las_Asignaciones_sin_filtro_de_Servicio()
    {
        var src = AccesosAAsignaciones.MedirSrc();
        var sql = AccesosAAsignaciones.MedirMigraciones();

        VolcarInventarioSiSePide(src, sql);

        // Control de que el instrumento observa: hay accesos en C# de las dos tablas, hay accesos SQL, y
        // el clasificador ve filtros de Servicio (si dejara de verlos, todo pasaría a crudo y saldría
        // como NUEVA; si los viera en todo, la lista entera saldría OBSOLETA).
        src.Select(a => a.Tabla).Distinct().Should().BeEquivalentTo(
            [AccesosAAsignaciones.Operacion, AccesosAAsignaciones.Cartera], "el escaneo de src ve las dos tablas");
        src.Should().Contain(a => a.FiltraPorServicio, "hay consultas que ya filtran por Servicio");
        sql.Should().NotBeEmpty("hay funciones y políticas vigentes que leen las tablas");

        var medido = src.Concat(sql)
            .Where(a => !a.FiltraPorServicio)
            .GroupBy(a => a.Clave)
            .ToDictionary(g => g.Key, g => g.Count());

        ListaCongelada.Verificar(Lista, medido, Guia).Should().BeNull();
    }

    // ------------------------------------------------------------------ control positivo, C#

    private static IReadOnlyList<AccesoAAsignacion> MedirCs(string cuerpo) =>
        AccesosAAsignaciones.EnCSharp(new Dictionary<string, string>
        {
            ["src/X/Lector.cs"] = "using AO = CaeManager.Domain.Operaciones.AsignacionOperacion;\n" +
                                  "class Lector {\n" + cuerpo + "\n}",
        });

    [Theory]
    [InlineData("Task<int> A() => ctx.AsignacionesOperacion.CountAsync();", "Lector.A · AsignacionesOperacion")]
    [InlineData("Task<int> A() => ctx.AsignacionesCartera.CountAsync(c => c.UsuarioId == u);", "Lector.A · AsignacionesCartera")]
    [InlineData("IQueryable<AsignacionOperacion> T => Set<AsignacionOperacion>();", "Lector.T · AsignacionesOperacion")]
    [InlineData("IQueryable<AO> T => Set<AO>();", "Lector.T · AsignacionesOperacion")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => o.Servicio2 == s); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("void A() { var q = ctx.AsignacionesOperacion; q = q.Where(o => o.Servicio == s); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => Catalogo.Servicio == s); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("void A() { db.Database.ExecuteSqlRaw(\"DELETE FROM \\\"AsignacionesCartera\\\" WHERE 1=1\"); }", "Lector.A · AsignacionesCartera")]
    [InlineData("Lector() { _ = ctx.AsignacionesCartera.ToList(); }", "Lector..ctor · AsignacionesCartera")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => request.Servicio == s); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("static bool P(AsignacionOperacion o) => o.Servicio == S;\nvoid A() { var n = ctx.AsignacionesOperacion.AsEnumerable().Count(P); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("void A() { db.Database.ExecuteSqlRaw(\"TRUNCATE \\\"AsignacionesCartera\\\"\"); }", "Lector.A · AsignacionesCartera")]
    [InlineData("void A() { db.Database.ExecuteSqlRaw(\"UPDATE \\\"AsignacionesOperacion\\\" SET \\\"Servicio\\\" = 'Inbound'\"); }", "Lector.A · AsignacionesOperacion")]
    public void Un_acceso_sin_filtro_de_Servicio_es_crudo(string cuerpo, string simbolo)
    {
        var accesos = MedirCs(cuerpo);

        accesos.Should().ContainSingle("el instrumento tiene que ver el acceso").Which.FiltraPorServicio.Should().BeFalse();
        accesos[0].Clave.Should().Be(new Ubicacion("src/X/Lector.cs", simbolo));
    }

    [Fact]
    public void Una_lambda_con_cuerpo_de_bloque_no_presta_el_filtro_entre_sus_sentencias()
    {
        var accesos = MedirCs(
            "Task A() => puerta.EjecutarAsync(async () => {\n" +
            "  var a = await ctx.AsignacionesOperacion.CountAsync(o => o.Servicio == s);\n" +
            "  var b = await ctx.AsignacionesOperacion.CountAsync();\n" +
            "});");

        accesos.Should().HaveCount(2);
        accesos.Count(a => a.FiltraPorServicio).Should().Be(1, "solo la sentencia que compara el servicio filtra");
        accesos.Count(a => !a.FiltraPorServicio).Should().Be(1, "la sentencia hermana sin filtro es cruda");
    }

    [Theory]
    [InlineData("Task<int> A() => ctx.AsignacionesOperacion.CountAsync(o => o.Servicio == ServicioCae.Inbound);")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => o.Servicio != s); }")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => o.Servicio is ServicioCae.Outbound); }")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => lista.Contains(o.Servicio)); }")]
    [InlineData("void A() { var n = (from c in ctx.AsignacionesCartera join o in ops on c.AsignacionOperacionId equals o.Id where o.Servicio == s select c).Count(); }")]
    [InlineData("static Expression<Func<AsignacionOperacion, bool>> P() => o => o.Servicio == S;\n" +
                "void A() { var n = ctx.AsignacionesOperacion.Count(P()); }")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(Otro.Predicado); }")]
    [InlineData("void A() { db.Database.ExecuteSqlRaw(\"SELECT 1 FROM \\\"AsignacionesOperacion\\\" WHERE \\\"Servicio\\\" = 'Inbound'\"); }")]
    public void Un_acceso_con_filtro_de_Servicio_no_es_crudo(string cuerpo)
    {
        var accesos = AccesosAAsignaciones.EnCSharp(new Dictionary<string, string>
        {
            ["src/X/Lector.cs"] = "class Lector {\n" + cuerpo + "\n}",
            ["src/X/Otro.cs"] = "class Otro { public static readonly Expression<Func<AsignacionOperacion, bool>> Predicado = o => o.Servicio == S; }",
        });

        accesos.Should().NotBeEmpty("el instrumento tiene que ver el acceso");
        accesos.Should().OnlyContain(a => a.FiltraPorServicio);
    }

    [Theory]
    [InlineData("// ctx.AsignacionesOperacion.ToList();\nvoid A() { }")]
    [InlineData("/// <see cref=\"AsignacionesOperacion\"/>\nvoid A() { }")]
    [InlineData("public DbSet<AsignacionOperacion> AsignacionesOperacion { get; }")]
    [InlineData("void A() { builder.ToTable(\"AsignacionesOperacion\"); }")]
    [InlineData("void A() { var x = AsignacionOperacion.Raiz(t, s, d, a); }")]
    public void Lo_que_no_lee_la_tabla_no_es_un_acceso(string cuerpo)
    {
        MedirCs(cuerpo).Should().BeEmpty();
    }

    [Fact]
    public void En_razor_todo_acceso_es_crudo_y_los_comentarios_no_cuentan()
    {
        var accesos = AccesosAAsignaciones.EnCSharp(new Dictionary<string, string>
        {
            ["src/Web/P.razor"] = "@* Db.AsignacionesCartera *@\n<p>x</p>\n@code { void A() { _ = Db.AsignacionesCartera.ToList(); } }",
        });

        accesos.Should().ContainSingle().Which.Should().Be(
            new AccesoAAsignacion("src/Web/P.razor", "(razor)", AccesosAAsignaciones.Cartera, "razor", false, 3));
    }

    // ------------------------------------------------------------------ control positivo, SQL

    private const string FuncionSinServicio =
        "CREATE OR REPLACE FUNCTION public.app_lee(p uuid) RETURNS int LANGUAGE sql AS $$ " +
        "SELECT count(*) FROM public.\"AsignacionesOperacion\" o WHERE o.\"PropietarioTenantId\" = p $$;";

    private const string FuncionConServicio =
        "CREATE OR REPLACE FUNCTION public.app_lee(p uuid) RETURNS int LANGUAGE sql AS $$ " +
        "SELECT count(*) FROM public.\"AsignacionesOperacion\" o WHERE o.\"Servicio\" = 'Inbound' $$;";

    [Fact]
    public void Una_funcion_vigente_que_lee_la_tabla_sin_Servicio_es_cruda()
    {
        var accesos = AccesosAAsignaciones.EnSql([("1", FuncionSinServicio)]);

        accesos.Should().ContainSingle().Which.Should().Be(
            new AccesoAAsignacion("SQL función public.app_lee", string.Empty, AccesosAAsignaciones.Operacion, "función", false, 0));
    }

    [Fact]
    public void El_Servicio_de_otra_sentencia_del_cuerpo_no_filtra_y_cada_lectura_cuenta()
    {
        const string dosSentencias =
            "CREATE OR REPLACE FUNCTION public.app_lee(p uuid) RETURNS int LANGUAGE plpgsql AS $$ BEGIN " +
            "PERFORM 1 FROM public.\"AsignacionesOperacion\" o WHERE o.\"PropietarioTenantId\" = p; " +
            "PERFORM 1 FROM public.\"AsignacionesOperacion\" o WHERE o.\"Servicio\" = 'Inbound'; " +
            "PERFORM 1 FROM public.\"AsignacionesOperacion\" o WHERE o.\"Id\" = p; RETURN 1; END $$;";

        var accesos = AccesosAAsignaciones.EnSql([("1", dosSentencias)]);

        accesos.Should().HaveCount(3, "una aparición por lectura, no una por objeto");
        accesos.Count(a => !a.FiltraPorServicio).Should().Be(2, "el Servicio de la segunda sentencia no filtra la primera ni la tercera");
    }

    [Fact]
    public void Cuenta_la_ultima_definicion_y_una_funcion_eliminada_deja_de_contar()
    {
        AccesosAAsignaciones.EnSql([("1", FuncionSinServicio), ("2", FuncionConServicio)])
            .Should().ContainSingle().Which.FiltraPorServicio.Should().BeTrue("la definición vigente filtra");

        AccesosAAsignaciones.EnSql([("1", FuncionConServicio), ("2", FuncionSinServicio)])
            .Should().ContainSingle().Which.FiltraPorServicio.Should().BeFalse("la redefinición posterior quita el filtro");

        AccesosAAsignaciones.EnSql([("1", FuncionSinServicio), ("2", "DROP FUNCTION IF EXISTS public.app_lee(uuid), public.app_otra(text) CASCADE;")])
            .Should().BeEmpty("la función ya no existe");

        AccesosAAsignaciones.EnSql([("1", FuncionSinServicio + "\n-- DROP FUNCTION public.app_lee(uuid);")])
            .Should().ContainSingle("un DROP en un comentario no elimina nada");
    }

    [Fact]
    public void Una_politica_que_lee_la_tabla_cuenta_y_la_tabla_de_su_ON_no()
    {
        const string sobreLaTabla =
            "CREATE POLICY posicion ON public.\"AsignacionesCartera\" USING (\"UsuarioId\" = app_usuario());";
        const string leeLaCartera =
            "CREATE POLICY cuentas_lectura ON public.\"Documentos\" USING (EXISTS (SELECT 1 FROM public.\"AsignacionesCartera\" c WHERE c.\"UsuarioId\" = app_usuario()));";

        AccesosAAsignaciones.EnSql([("1", sobreLaTabla)]).Should().BeEmpty("la tabla del ON no se lee en la expresión");

        AccesosAAsignaciones.EnSql([("1", leeLaCartera)]).Should().ContainSingle().Which.Clave.Should().Be(
            new Ubicacion("SQL política cuentas_lectura ON public.Documentos", AccesosAAsignaciones.Cartera));

        AccesosAAsignaciones.EnSql([("1", leeLaCartera), ("2", "DROP POLICY IF EXISTS cuentas_lectura ON public.\"Documentos\";")])
            .Should().BeEmpty("la política eliminada no cuenta");

        AccesosAAsignaciones.EnSql([("1", sobreLaTabla), ("2", "ALTER POLICY posicion ON public.\"AsignacionesCartera\" USING (EXISTS (SELECT 1 FROM public.\"AsignacionesOperacion\" o));")])
            .Should().ContainSingle("un ALTER POLICY que añade una lectura cuenta").Which.Tabla.Should().Be(AccesosAAsignaciones.Operacion);
    }

    // ------------------------------------------------------------------ volcado para el inventario

    /// <summary>
    /// Con <c>HYDRA_TRINQUETES_VOLCAR</c>, además de la lista de crudos que vuelca
    /// <see cref="ListaCongelada"/>, escribe el inventario completo (filtrados incluidos, con su línea) para
    /// el documento del inventario. No cambia el veredicto.
    /// </summary>
    private static void VolcarInventarioSiSePide(IReadOnlyList<AccesoAAsignacion> src, IReadOnlyList<AccesoAAsignacion> sql)
    {
        var directorio = Environment.GetEnvironmentVariable("HYDRA_TRINQUETES_VOLCAR");
        if (string.IsNullOrWhiteSpace(directorio)) return;

        Directory.CreateDirectory(directorio);
        var lineas = src.Concat(sql).Select(a =>
            $"{(a.FiltraPorServicio ? "filtra" : "CRUDO ")}\t{a.Forma}\t{a.Lugar}:{a.Linea}\t{a.Miembro}\t{a.Tabla}");
        File.WriteAllLines(Path.Combine(directorio, Lista + "-inventario-completo.tsv"), lineas);
    }
}
