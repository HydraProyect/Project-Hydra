using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Claves <c>@key</c> únicas por construcción</b> (continuación de #1064). Dos <c>@key</c> hermanas iguales hacen que el diff de
/// Blazor lance «Attempting to return wrong pooled instance» y MATA EL CIRCUITO (~1 s tras conectar): la landing del Coordinador CAE
/// lo sufrió porque <c>ItemBandejaDto.Id</c> —una cadena construida a mano— no llevaba el Centro, y nada garantizaba que el
/// siguiente constructor del Id, o la siguiente lista con <c>@key</c> compuesta, llevara todas las dimensiones de su fila.
///
/// <para>
/// Dos trinquetes por ubicación (<see cref="ListaCongelada"/>), uno por cada punto único que garantiza la unicidad:
/// <list type="number">
/// <item><b>El Id de la fila de la cola nace en <c>IdDeFilaDeCola</c></b> (Application): todo <c>new ItemBandejaDto(…)</c> de
/// <c>src</c> recibe su Id de ese punto (o de <c>AlertaDto.IdDeFila</c>, que delega en él). Una cadena interpolada escrita en un
/// productor nuevo es una línea que falta en la lista.</item>
/// <item><b>Toda <c>@key</c> construida de un <c>.razor</c> pasa por <c>ClavesDeHermanos.De(…)</c></b> (Web), que numera las
/// repeticiones entre hermanos. Se clasifica cada <c>@key</c>: <i>entidad</i> (una propiedad cuyo nombre termina en <c>Id</c>:
/// <c>documento.Id</c>, <c>trabajador.TrabajadorId</c>), <i>punto único</i> (<c>claves.De(…)</c>) o <i>construida</i> (todo lo
/// demás: tupla, anónimo, interpolación, <c>grupo.Clave</c>, <c>indicador.Tipo</c>, una llamada). Las construidas que no pasan por
/// el punto único están congeladas por fichero y expresión: añadir una es una línea nueva, migrar una al punto único baja la
/// lista. Además, todo <c>&lt;PanelResolverItem&gt;</c> —que pinta un <c>ItemBandejaDto</c>— con <c>@key</c> se keya por el punto único,
/// sin lista que lo excuse (sin <c>@key</c> no compite con nadie: el diff es posicional).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> (a) «Entidad» es una clasificación por <i>nombre</i>: una propiedad
/// <c>Id</c> que en realidad es una cadena construida (el caso de <c>ItemBandejaDto.Id</c>, que no se ve desde el texto del
/// <c>.razor</c>) solo se detecta por la regla de <c>&lt;PanelResolverItem&gt;</c> y por <c>.Item.Id</c>. Que una lista keyed por un
/// <c>Id</c> de entidad no repita es una propiedad de su consulta (clave de BD, o una fila por entidad), no de este trinquete.
/// (b) La congelada incluye <c>@key</c> que no compiten con hermanos (reinicio de un único hijo: <c>_version</c>,
/// <c>GeneracionDe(…)</c>): son inocuas y se listan porque, desde el texto, no se distinguen de una construida en un bucle.
/// (c) No ve un <c>SetKey</c> escrito en C# (<c>BuildRenderTree</c> a mano); hoy no hay ninguno en <c>src</c>. (d) Del Id de
/// <c>ItemBandejaDto</c> ve el <c>new ItemBandejaDto(…)</c> explícito, el <c>new(Id: …)</c> de tipo inferido con los argumentos
/// <c>Id:</c> con nombre y <c>with { Id = … }</c>; NO ve un <c>new(…)</c> posicional de tipo inferido ni un <c>.razor</c> que construya el ítem.
/// </para>
/// </summary>
public class ClavesDeListaUnicasPorConstruccionTests
{
    private const string ListaIds = "Id-de-fila-fuera-del-punto-unico";
    private const string ListaKeys = "Key-construida-fuera-del-punto-unico";

    private const string GuiaIds =
        "El Id de una fila de la cola se construye en IdDeFilaDeCola (src/CaeManager.Application/Common): un constructor por tipo de fila con " +
        "todas sus dimensiones. No escribas el Id a mano en el productor. Si es una instancia que no va a una lista (un ítem de prueba que solo " +
        "sirve para calcular un tono), no sube la lista: pásale un Id de IdDeFilaDeCola o añade la línea con el motivo en tests/CaeManager.Architecture.Tests/Congelados/" + ListaIds + ".txt.";

    private const string GuiaKeys =
        "Una @key construida se pasa por ClavesDeHermanos: `@{ var claves = new ClavesDeHermanos(); }` en el contenedor de los hermanos y " +
        "`@key=\"claves.De(identidad)\"` (numera las repeticiones y no deja morir el circuito). Si migras una, baja o borra su línea en " +
        "tests/CaeManager.Architecture.Tests/Congelados/" + ListaKeys + ".txt (HYDRA_TRINQUETES_VOLCAR=<directorio> vuelca la medida). No añadas una línea para una @key nueva en un bucle.";

    // ---------------------------------------------------------------- 1. Id de ItemBandejaDto

    [Fact]
    public void Todo_ItemBandejaDto_de_src_recibe_su_Id_del_punto_unico_salvo_los_congelados()
    {
        var medido = new Dictionary<Ubicacion, int>();
        var construcciones = 0;

        foreach (var archivo in FuentesDeSrc.Archivos().Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var ruta = FuentesDeSrc.Relativa(archivo);
            var (total, fuera) = MedirItemIds(File.ReadAllText(archivo));
            construcciones += total;
            if (fuera > 0)
                medido[new Ubicacion(ruta, "ItemBandejaDto con Id fuera de IdDeFilaDeCola")] = fuera;
        }

        // Control positivo: si el recorrido no viera las construcciones reales, «ninguna fuera del punto único» valdría por vacío.
        construcciones.Should().BeGreaterThanOrEqualTo(3, "había 11 new ItemBandejaDto en src al escribirlo (7 en Fusionar, 3 en Mi trabajo, 1 en TipoItemBandejaUi): el suelo solo dice que el recorrido ve productores; no es un recuento a conservar");

        ListaCongelada.Verificar(ListaIds, medido, GuiaIds).Should().BeNull();
    }

    /// <summary>
    /// Cuántos <c>new ItemBandejaDto(…)</c> hay en el texto y cuántos reciben su Id (primer argumento o <c>Id:</c> con nombre) de algo
    /// que no es <c>IdDeFilaDeCola.X(…)</c> ni <c>algo.IdDeFila(…)</c>. También cuenta <c>… with { Id = … }</c> (cambiar el Id de un
    /// ítem ya hecho): desde el texto no se sabe de qué tipo es el receptor, así que un <c>with</c> que asigne <c>Id</c> cuenta siempre.
    /// </summary>
    internal static (int Total, int FueraDelPuntoUnico) MedirItemIds(string texto)
    {
        var raiz = CSharpSyntaxTree.ParseText(texto).GetRoot();
        var total = 0;
        var fuera = 0;

        foreach (var creacion in raiz.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            SeparatedSyntaxList<ArgumentSyntax> argumentos;
            switch (creacion)
            {
                case ObjectCreationExpressionSyntax explicita when NombreSimple(explicita.Type) == "ItemBandejaDto":
                    argumentos = explicita.ArgumentList?.Arguments ?? default;
                    break;
                // `new(Id: ..., Tipo: ..., Titulo: ...)`: sin semántica no se sabe el tipo destino. Se cuenta si nombra a la vez
                // `Id:`, `Tipo:` y `Titulo:` (otros records con `Id:` nombrado, p. ej. las órdenes del asistente, no se confunden);
                // un `new(...)` posicional con tipo inferido NO se ve (contrato efectivo).
                case ImplicitObjectCreationExpressionSyntax implicita
                    when new[] { "Id", "Tipo", "Titulo" }.All(n => implicita.ArgumentList.Arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == n)):
                    argumentos = implicita.ArgumentList.Arguments;
                    break;
                default:
                    continue;
            }

            total++;
            var id = argumentos.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "Id")
                     ?? argumentos.FirstOrDefault(a => a.NameColon is null);
            if (id is null || !EsDelPuntoUnico(id.Expression))
                fuera++;
        }

        foreach (var con in raiz.DescendantNodes().OfType<WithExpressionSyntax>())
        {
            var asignaId = con.Initializer.Expressions.OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "Id" });
            if (asignaId)
                fuera++;
        }

        return (total, fuera);
    }

    private static string NombreSimple(TypeSyntax tipo) => tipo switch
    {
        IdentifierNameSyntax i => i.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        _ => tipo.ToString(),
    };

    private static bool EsDelPuntoUnico(ExpressionSyntax expresion) =>
        expresion is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax acceso }
        && (acceso.Expression is IdentifierNameSyntax { Identifier.ValueText: "IdDeFilaDeCola" }
            || acceso.Name.Identifier.ValueText == "IdDeFila");

    [Fact]
    public void El_detector_de_Ids_ve_una_cadena_a_mano_un_with_y_acepta_el_punto_unico()
    {
        // Control positivo: el trinquete no puede dar verde porque el detector no distinga nada.
        MedirItemIds("class C { object X(Guid g) => new ItemBandejaDto(Id: $\"alerta-{g}\", Tipo: 1); }")
            .Should().Be((1, 1), "una interpolación a mano en Id: es lo que #1064 convirtió en defecto");
        MedirItemIds("class C { object X(Guid g) => new ItemBandejaDto($\"alerta-{g}\", 1); }")
            .Should().Be((1, 1), "posicional: el primer argumento es el Id");
        MedirItemIds("class C { object X() => new ItemBandejaDto(Id: \"\", Tipo: 1); }").Should().Be((1, 1));
        MedirItemIds("class C { object X(ItemBandejaDto i) => i with { Id = \"otro\" }; }").Should().Be((0, 1));

        MedirItemIds("class C { ItemBandejaDto X(Guid g) => new(Id: $\"alerta-{g}\", Tipo: 1, Titulo: \"t\"); }").Should().Be((1, 1), "tipo inferido con Id: con nombre");
        MedirItemIds("class C { ItemBandejaDto X(Guid g) => new(Id: IdDeFilaDeCola.Visita(g), Tipo: 1, Titulo: \"t\"); }").Should().Be((1, 0));
        MedirItemIds("class C { Otra X() => new(Id: \"x\", Nombre: \"y\"); }").Should().Be((0, 0), "otro record con Id: con nombre no es una fila de la cola");
        MedirItemIds("class C { Otro X() => new(1, 2); }").Should().Be((0, 0), "un new(...) posicional de tipo inferido no se ve: contrato efectivo");

        MedirItemIds("class C { object X(Guid g) => new ItemBandejaDto(Id: IdDeFilaDeCola.Visita(g), Tipo: 1); }").Should().Be((1, 0));
        MedirItemIds("class C { object X(A a) => new ItemBandejaDto(Id: a.IdDeFila(\"alerta\"), Tipo: 1); }").Should().Be((1, 0));
        MedirItemIds("class C { object X(Guid g) => new Otro(Id: $\"x-{g}\"); }").Should().Be((0, 0));
        MedirItemIds("class C { object X(Guid g) => new CaeManager.Application.ItemBandejaDto(IdDeFilaDeCola.Revision(g), 1); }").Should().Be((1, 0));
    }

    // ---------------------------------------------------------------- 2. @key construidas en .razor

    internal enum ClaseDeKey { Entidad, PuntoUnico, Construida }

    private static readonly Regex ExpresionSimple = new(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$", RegexOptions.Compiled);
    private static readonly Regex PuntoUnico = new(@"^claves\w*\.De\(", RegexOptions.Compiled);
    private static readonly Regex ItemDeFila = new(@"(^|\.)Item\.Id$", RegexOptions.Compiled);

    internal static ClaseDeKey Clasificar(string expresion)
    {
        var e = expresion.Trim();
        if (e.StartsWith("@(", StringComparison.Ordinal) && e.EndsWith(')'))
            e = e[2..^1].Trim();

        if (PuntoUnico.IsMatch(e))
            return ClaseDeKey.PuntoUnico;

        if (ExpresionSimple.IsMatch(e) && !ItemDeFila.IsMatch(e))
        {
            var ultimo = e[(e.LastIndexOf('.') + 1)..];
            if (ultimo == "Id" || (ultimo.Length > 2 && ultimo.EndsWith("Id", StringComparison.Ordinal) && char.IsLower(ultimo[^3])))
                return ClaseDeKey.Entidad;
        }

        return ClaseDeKey.Construida;
    }

    /// <summary>Las expresiones de todos los <c>@key="…"</c> del marcado (ya sin comentarios), en orden de aparición.</summary>
    internal static IReadOnlyList<string> Keys(string textoSinComentarios)
    {
        var resultado = new List<string>();
        foreach (Match m in Regex.Matches(textoSinComentarios, @"@key\s*=\s*""", RegexOptions.CultureInvariant))
        {
            var inicio = m.Index + m.Length;
            resultado.Add(MarcadoRazor.ValorDeComillas(textoSinComentarios, inicio));
        }

        return resultado;
    }

    private static string Compactar(string expresion) => Regex.Replace(expresion.Trim(), @"\s+", " ");

    [Fact]
    public void Ninguna_key_construida_de_un_razor_queda_fuera_del_punto_unico_salvo_las_congeladas()
    {
        var medido = new Dictionary<Ubicacion, int>();
        var clases = new Dictionary<ClaseDeKey, int>();
        var ficheros = 0;

        foreach (var (ruta, contenido) in MarcadoRazor.LeerRazorDeLaWeb())
        {
            ficheros++;
            foreach (var expresion in Keys(LimpiadorDeComentarios.Quitar(contenido, razor: true)))
            {
                var clase = Clasificar(expresion);
                clases[clase] = clases.GetValueOrDefault(clase) + 1;
                if (clase != ClaseDeKey.Construida) continue;

                var ubicacion = new Ubicacion(ruta, Compactar(expresion));
                medido[ubicacion] = medido.GetValueOrDefault(ubicacion) + 1;
            }
        }

        // Control positivo: el recorrido ve el árbol entero y reparte entre las tres clases. Sin él, «ninguna fuera del punto único»
        // valdría por vacío (p. ej. si la enumeración de .razor se rompiera, todas las líneas de la lista pasarían a OBSOLETA a la vez).
        ficheros.Should().BeGreaterThan(150, "había 223 .razor en la Web al escribirlo");
        clases.GetValueOrDefault(ClaseDeKey.Entidad).Should().BeGreaterThan(30, "había ~70 @key de entidad al escribirlo");
        clases.GetValueOrDefault(ClaseDeKey.PuntoUnico).Should().BeGreaterThanOrEqualTo(1, "hay @key por el punto único (Mi trabajo, GrupoCola, Bandeja…): si no se ve ninguna, el clasificador dejó de reconocerlo");

        ListaCongelada.Verificar(ListaKeys, medido, GuiaKeys).Should().BeNull();
    }

    [Fact]
    public void Todo_PanelResolverItem_con_key_se_keya_por_el_punto_unico()
    {
        var problemas = new List<string>();
        var vistos = 0;

        foreach (var (ruta, contenido) in MarcadoRazor.LeerRazorDeLaWeb())
        {
            var texto = LimpiadorDeComentarios.Quitar(contenido, razor: true);
            foreach (var apertura in MarcadoRazor.Aperturas(texto, "PanelResolverItem"))
            {
                vistos++;
                if (!PanelResolverItemSinKeyAPelo(apertura.Texto))
                    problemas.Add($"{ruta}: <PanelResolverItem> con @key que no es claves.De(…) (un ItemBandejaDto.Id es una cadena construida: su unicidad no está garantizada)");
            }
        }

        vistos.Should().BeGreaterThanOrEqualTo(1, "hay <PanelResolverItem> en GrupoCola, Bandeja e Inicio: si no se ve ninguno, el recorrido dejó de mirar");
        string.Join(Environment.NewLine, problemas).Should().BeEmpty();
    }

    internal static bool PanelResolverItemSinKeyAPelo(string aperturaDelElemento) =>
        Keys(aperturaDelElemento).All(k => Clasificar(k) == ClaseDeKey.PuntoUnico);

    private static readonly Regex UsoDeFuente = new(@"\b(claves\w*)\.De\(", RegexOptions.Compiled);

    /// <summary>Nombres de fuente de claves que el marcado usa (<c>claves.De(…)</c>) sin declararlos allí como <c>claves = new ClavesDeHermanos()</c>.</summary>
    internal static IReadOnlyList<string> FuentesSinDeclarar(string textoSinComentarios) => UsoDeFuente.Matches(textoSinComentarios)
        .Select(m => m.Groups[1].Value)
        .Distinct()
        .Where(nombre => !Regex.IsMatch(textoSinComentarios, $@"\b{nombre}\s*=\s*new\s+ClavesDeHermanos\s*\(\s*\)"))
        .ToList();

    [Fact]
    public void Toda_fuente_de_claves_se_crea_en_el_propio_marcado()
    {
        // Un campo (o un static) acumularía repeticiones entre pintados: las claves cambiarían en cada render y se recrearía todo.
        var problemas = new List<string>();
        var usos = 0;

        foreach (var (ruta, contenido) in MarcadoRazor.LeerRazorDeLaWeb())
        {
            var texto = LimpiadorDeComentarios.Quitar(contenido, razor: true);
            usos += UsoDeFuente.Matches(texto).Count;
            problemas.AddRange(FuentesSinDeclarar(texto).Select(n => $"{ruta}: usa {n}.De(…) sin declarar `{n} = new ClavesDeHermanos()` en el marcado"));
        }

        usos.Should().BeGreaterThanOrEqualTo(1, "hay @key por el punto único: si no se ve ningún uso, el recorrido dejó de mirar");
        string.Join(Environment.NewLine, problemas).Should().BeEmpty();
    }

    [Fact]
    public void El_detector_de_fuentes_ve_la_que_falta_y_acepta_la_declarada()
    {
        FuentesSinDeclarar("@{ var claves = new ClavesDeHermanos(); } <li @key=\"claves.De(a)\">").Should().BeEmpty();
        FuentesSinDeclarar("var claves = new ClavesDeHermanos();\n<li @key=\"claves.De(a)\"><li @key=\"clavesGrupos.De(a)\">").Should().Equal("clavesGrupos");
        FuentesSinDeclarar("<li @key=\"claves.De(a)\">").Should().Equal("claves");
        FuentesSinDeclarar("<li @key=\"a.Id\">").Should().BeEmpty();
    }

    [Fact]
    public void El_clasificador_de_keys_distingue_entidad_punto_unico_y_construida()
    {
        // Control positivo del instrumento, con las formas reales del repositorio.
        foreach (var entidad in new[] { "documento.Id", "trabajador.TrabajadorId", "lote.Destinatarios.ContactoId", "_detalle.Id", "documento.AcreditacionId" })
            Clasificar(entidad).Should().Be(ClaseDeKey.Entidad, entidad);

        foreach (var puntoUnico in new[] { "claves.De(item.Id)", "clavesGrupos.De(grupo.Clave)", "@(claves.De((fila.TenantId, fila.Item.Id)))" })
            Clasificar(puntoUnico).Should().Be(ClaseDeKey.PuntoUnico, puntoUnico);

        foreach (var construida in new[]
                 {
                     "(exigencia.CentroId, exigencia.Documento.TipoDocumentoId)", "new { sugerencia.CentroId, sugerencia.TipoDocumentoId }",
                     "@(\"lote-\" + lote.Clave)", "grupo.Clave", "indicador.Tipo", "GeneracionDe(clave)", "bandeja.Grupos[i].GrupoId",
                     "fila.Item.Id", "Item.Id", "$\"{a.Id}-{b.Id}\"", "claveDeOtraCosa.Valor", "claves.Otra(x)", "tipo.Valid", "x.Paid",
                 })
            Clasificar(construida).Should().Be(ClaseDeKey.Construida, construida);
    }

    [Fact]
    public void El_extractor_de_keys_ve_las_formas_de_atributo_del_repositorio()
    {
        Keys("<li @key=\"a.Id\"></li>").Should().Equal("a.Id");
        Keys("<X\n   @key=\"claves.De((a.B, a.C))\"\n   Y=\"1\" />").Should().Equal("claves.De((a.B, a.C))");
        Keys("<div @key=\"@(\"lote-\" + l.Clave)\">").Should().Equal("@(\"lote-\" + l.Clave)");
        Keys("<p>sin keys</p>").Should().BeEmpty();
    }

    [Fact]
    public void El_detector_de_PanelResolverItem_acepta_el_punto_unico_y_rechaza_la_key_a_pelo()
    {
        PanelResolverItemSinKeyAPelo("<PanelResolverItem @key=\"claves.De(item.Id)\" Item=\"item\" />").Should().BeTrue();
        PanelResolverItemSinKeyAPelo("<PanelResolverItem @key=\"item.Id\" Item=\"item\" />").Should().BeFalse();
        PanelResolverItemSinKeyAPelo("<PanelResolverItem @key=\"fila.Item.Id\" Item=\"item\" />").Should().BeFalse();
        PanelResolverItemSinKeyAPelo("<PanelResolverItem Item=\"item\" />").Should().BeTrue("sin @key no compite con nadie");
    }
}
