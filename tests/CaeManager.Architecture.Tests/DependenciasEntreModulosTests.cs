using System.Text;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using Xunit.Abstractions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// ADR-013, paso M0b (§ 4.3 test 2; § 4.7 mecanismo 2): las aristas entre módulos, leídas del IL de los ensamblados de
/// <c>src</c> con el registro de M0a (<see cref="RegistroDeModulos"/>), congeladas <b>por dirección</b> en
/// <c>Congelados/Modulos-*.txt</c> con el formato de <see cref="ListaCongelada"/>. Una arista nueva da rojo
/// (<c>NUEVA</c> o <c>CRECE</c>); una baja obliga a borrar la línea (<c>BAJA</c> u <c>OBSOLETA</c>): la deuda solo baja.
///
/// <para>
/// <b>Direcciones prohibidas</b> (ADR-013 § 4.1, reglas 1 a 3; D-F2 para Facturación): Inbound -> Outbound (cerrada, solo
/// K-5 hasta B-22), Outbound -> Inbound (cerrada y vacía desde que M2 cerró K-2), Núcleo -> Inbound (deuda que solo baja;
/// queda K-3) y Núcleo -> Outbound (deuda que solo baja), Facturación
/// -> fuera del Núcleo y «nadie depende de Facturación». Permitidas y no listadas: todos -> Núcleo, y el punto de
/// composición, que puede referenciar todos los módulos (regla 4). Las referencias a <c>Composicion</c> se miden y se
/// informan, no se congelan.
/// </para>
///
/// <para>
/// <b>Formato de las líneas.</b> <c>tipo de origen :: Módulo: tipo de destino = 1</c> (una por arista), salvo la lista
/// Núcleo -> Outbound, que por tamaño va por módulo, <c>tipo de origen :: -&gt; Outbound = n</c>, con <c>n</c> = tipos
/// de destino distintos (K-16, ADR-013 § 2.4). Un tipo es el tipo raíz (un fichero salvo los auxiliares de Razor).
/// </para>
///
/// <para>
/// <b>Lo que estos tests no ven</b> (ADR-013 § 4.3, declarado aquí como límite conocido): una petición de MediatR
/// construida por reflexión o por nombre, SQL en cadenas fuera de las migraciones, una columna <c>Guid</c> sin FK que
/// apunte a otro módulo (K-9), un <c>cref</c> de documentación (K-6) y los literales que nombran features (K-8). Los
/// controles positivos son tipos C# en este ensamblado, no componentes Razor ni código de Roslyn; <b>Composición</b> como
/// destino se mide y se informa, pero no se congela; y la lista Núcleo -> Outbound, por módulo (<c>Grano.Modulo</c>), no
/// detecta que una arista se sustituya por otra del mismo tipo de origen y recuento. El
/// registro es provisional en la mayoría de las áreas del Núcleo (<c>provisional</c> en <c>Modulos-registro.txt</c>): si
/// una decisión de ADR-013 mueve un área, cambian las listas.
/// </para>
///
/// <para>
/// <b>Modo informe.</b> Con <c>HYDRA_TRINQUETES_VOLCAR=&lt;directorio&gt;</c> se escribe ahí cada lista medida y
/// <c>Modulos-aristas-informe.txt</c> (cifras por par de módulos y detalle de las direcciones prohibidas). Nunca reescribe
/// las listas versionadas.
/// </para>
/// </summary>
public class DependenciasEntreModulosTests(ITestOutputHelper salida)
{
    private enum Grano
    {
        /// <summary>Una línea por arista «tipo -> tipo».</summary>
        Tipo,

        /// <summary>Una línea por tipo de origen y módulo de destino; el recuento son los tipos de destino distintos.</summary>
        Modulo,
    }

    private sealed record Lista(string Nombre, (Modulo Origen, Modulo Destino)[] Pares, Grano Grano, string Guia);

    private static readonly Lista[] Listas =
    [
        new("Modulos-Inbound-a-Outbound", [(Modulo.Inbound, Modulo.Outbound)], Grano.Tipo,
            "Inbound -> Outbound: lista cerrada, no admite altas (ADR-013 § 4.3). Hoy solo K-5 (EvidenciaInbound reutiliza el " +
            "estado de acreditación de Outbound) hasta B-22. Sube al Núcleo lo que Inbound necesita; no añadas la línea."),
        new("Modulos-Outbound-a-Inbound", [(Modulo.Outbound, Modulo.Inbound)], Grano.Tipo,
            "Outbound -> Inbound: lista cerrada y vacía (ADR-013 § 4.3; K-2 se cerró en M2). Outbound no conoce a Inbound: " +
            "lo que ambos necesitan sube al Núcleo o entra por un puerto suyo; no añadas la línea."),
        new("Modulos-Nucleo-a-Inbound", [(Modulo.Nucleo, Modulo.Inbound)], Grano.Tipo,
            "Núcleo -> Inbound: prohibida (ADR-013 § 4.1, regla 3); la deuda (K-3; K-1 y K-17 se cerraron en M2) solo baja. Una línea que ya " +
            "no se mide se borra; una nueva no se añade: contribuye por un puerto del Núcleo (mecanismo 10)."),
        new("Modulos-Nucleo-a-Outbound", [(Modulo.Nucleo, Modulo.Outbound)], Grano.Modulo,
            "Núcleo -> Outbound: prohibida (ADR-013 § 4.1, regla 3); la deuda de K-16 solo baja (N-4). Una línea que ya no se " +
            "mide o cuyo recuento baja se corrige en el mismo PR; un tipo nuevo del Núcleo no puede referenciar Outbound."),
        new("Modulos-Facturacion-a-fuera-del-Nucleo", [(Modulo.Facturacion, Modulo.Outbound), (Modulo.Facturacion, Modulo.Inbound)], Grano.Tipo,
            "Facturación depende solo del Núcleo (D-F2). Para leer Outbound, un puerto del Núcleo como IFuenteLiquidacionOperadorCae; " +
            "la deuda anterior al módulo solo encoge."),
        new("Modulos-a-Facturacion",
            [(Modulo.Nucleo, Modulo.Facturacion), (Modulo.Outbound, Modulo.Facturacion), (Modulo.Inbound, Modulo.Facturacion)], Grano.Tipo,
            "Nadie depende de Facturación (D-F2). Lo que Outbound necesita de ella (el cierre mensual del cuadro de dirección) está " +
            "congelado y solo baja; lo nuevo entra por un puerto del Núcleo."),
    ];

    public static TheoryData<string> NombresDeListas => new(Listas.Select(l => l.Nombre));

    [Theory]
    [MemberData(nameof(NombresDeListas))]
    public void Las_aristas_prohibidas_entre_modulos_coinciden_con_su_lista_congelada(string nombre)
    {
        var lista = Listas.Single(l => l.Nombre == nombre);
        var medicion = MedicionDeSrc.Valor.Aristas;

        var fallo = ListaCongelada.Verificar(lista.Nombre, Medido(medicion, lista), lista.Guia);

        fallo.Should().BeNull();
    }

    /// <summary>
    /// Las direcciones que el ADR-013 prohíbe (§ 4.1 reglas 1 a 3, D-F2), fijadas aquí y no deducidas de <see cref="Listas"/>:
    /// quitar un par de una lista, o una lista entera, no puede dejar una dirección sin vigilar.
    /// </summary>
    private static readonly (Modulo Origen, Modulo Destino)[] ParesProhibidos =
    [
        (Modulo.Inbound, Modulo.Outbound),
        (Modulo.Outbound, Modulo.Inbound),
        (Modulo.Nucleo, Modulo.Inbound),
        (Modulo.Nucleo, Modulo.Outbound),
        (Modulo.Facturacion, Modulo.Outbound),
        (Modulo.Facturacion, Modulo.Inbound),
        (Modulo.Nucleo, Modulo.Facturacion),
        (Modulo.Outbound, Modulo.Facturacion),
        (Modulo.Inbound, Modulo.Facturacion),
    ];

    [Fact]
    public void Las_listas_vigilan_exactamente_las_direcciones_prohibidas_del_adr()
    {
        Listas.SelectMany(l => l.Pares).Should().BeEquivalentTo(ParesProhibidos,
            "quitar un par de una lista deja esa dirección sin vigilar; añadir uno exige decidirlo en el ADR, no aquí");
    }

    private static readonly string[] DestinosDeK5 = ["Domain.Documentos.EstadoAcreditacion", "Domain.Documentos.CausaRechazoAcreditacion"];

    /// <summary>
    /// «Cerrada» no es una promesa de la guía: el conjunto permitido está en código. Añadir a mano una línea nueva a
    /// <c>Modulos-Inbound-a-Outbound.txt</c> (la lista cerrada) no deja el test de la lista en verde por sí sola.
    /// </summary>
    [Fact]
    public void Inbound_a_Outbound_es_una_lista_cerrada_que_solo_admite_K5()
    {
        var lineas = ListaCongelada.Leer(File.ReadAllText(ListaCongelada.RutaDeLista("Modulos-Inbound-a-Outbound"))).Keys;

        foreach (var linea in lineas)
        {
            System.Text.RegularExpressions.Regex.IsMatch(linea.Lugar, @"\bEvidencias?Inbound").Should().BeTrue(
                $"solo la evidencia Inbound reutiliza el estado de acreditación (K-5): «{linea.Lugar}» no es de ese grupo");
            DestinosDeK5.Select(d => $"Outbound: {d}").Should().Contain(linea.Simbolo,
                "el único destino Outbound permitido es el estado de acreditación y su causa de rechazo (K-5, hasta B-22)");
        }
    }

    [Fact]
    public void Outbound_a_Inbound_es_una_lista_cerrada_y_vacia()
    {
        var lineas = ListaCongelada.Leer(File.ReadAllText(ListaCongelada.RutaDeLista("Modulos-Outbound-a-Inbound"))).Keys;

        lineas.Should().BeEmpty("Outbound -> Inbound quedó vacía al cerrar K-2 (ADR-013 M2) y no admite altas");
    }

    /// <summary>
    /// La deuda Núcleo -> Inbound (hoy solo K-3; K-1 y K-17 se cerraron en M2) también está fijada en código: solo baja. Límite declarado: las listas de
    /// Núcleo -> Outbound, de Facturación y hacia Facturación son deuda que solo baja sin conjunto permitido en código (es
    /// anterior al módulo), así que una arista nueva con su línea añadida a mano pone en verde el test de la lista y solo
    /// la revisión de la PR la detiene.
    /// </summary>
    [Fact]
    public void Nucleo_a_Inbound_solo_contiene_la_deuda_K3()
    {
        string[] permitidas =
        [
            "Infrastructure.FileStorage.DiskFileStorageService",
        ];

        var lineas = ListaCongelada.Leer(File.ReadAllText(ListaCongelada.RutaDeLista("Modulos-Nucleo-a-Inbound"))).Keys;

        lineas.Select(l => l.Lugar).Distinct().Should().BeSubsetOf(permitidas,
            "Núcleo -> Inbound solo baja: queda K-3 (almacén de ficheros); K-1 y K-17 se cerraron en M2 y no vuelven");
    }

    [Fact]
    public void El_instrumento_mira_todos_los_modulos_y_clasifica_todos_los_tipos_que_referencia()
    {
        var medicion = MedicionDeSrc.Valor.Aristas;

        foreach (var modulo in new[] { Modulo.Nucleo, Modulo.Outbound, Modulo.Inbound, Modulo.Facturacion, Modulo.Composicion })
            medicion.TiposDeOrigen(modulo).Should().BeGreaterThan(0, $"control del instrumento: tiene que medir tipos de {modulo}");

        // Un vacío no es cumplimiento: si el IL no se leyera, todo daría «sin aristas». Estos pares existen siempre.
        medicion.Cifras(Modulo.Outbound, Modulo.Nucleo).Aristas.Should().BeGreaterThan(100, "Outbound usa el Núcleo; si no, el IL no se lee");
        medicion.Cifras(Modulo.Inbound, Modulo.Nucleo).Aristas.Should().BeGreaterThan(5, "Inbound usa el Núcleo; si no, el IL no se lee");
        medicion.Cifras(Modulo.Facturacion, Modulo.Nucleo).Aristas.Should().BeGreaterThan(5, "Facturación usa el Núcleo; si no, el IL no se lee");

        medicion.OrigenesSinModulo.Should().BeEmpty("todo tipo de src tiene módulo (ModulosRegistradosTests); uno sin módulo no se mide");
        medicion.DestinosSinModulo.Should().BeEmpty("un tipo referido sin módulo escaparía a las listas");
    }

    /// <summary>
    /// Las aristas del cierre mensual frente al cuadro de dirección Outbound, que el registro anota (D-M1): el cuadro usa
    /// <c>ICierresCuadroDireccion</c> (Outbound -> Facturación) y la foto del cierre contiene la medición del cuadro
    /// (Facturación -> Outbound). Se miden y están congeladas en las listas; aquí se ancla que el instrumento las ve.
    /// </summary>
    [Fact]
    public void Las_aristas_del_cierre_mensual_frente_al_cuadro_de_direccion_se_miden_y_estan_congeladas()
    {
        var medicion = MedicionDeSrc.Valor.Aristas;

        var haciaFacturacion = medicion.De(Modulo.Outbound, Modulo.Facturacion);
        haciaFacturacion.Should().Contain(p => p.Value.Contains("Application.Dashboard.ICierresCuadroDireccion"),
            "el cuadro de dirección Outbound usa ICierresCuadroDireccion (Facturación)");

        var haciaOutbound = medicion.De(Modulo.Facturacion, Modulo.Outbound);
        haciaOutbound.Should().ContainKey("Application.Dashboard.FotoCierreMensual");
        haciaOutbound["Application.Dashboard.FotoCierreMensual"].Should().Contain("Application.Dashboard.Queries.MedicionCuadroDireccion",
            "la foto del cierre mensual contiene la medición del cuadro de dirección (Outbound)");

        var haciaFacturacionCongelado = ListaCongelada.Leer(File.ReadAllText(ListaCongelada.RutaDeLista("Modulos-a-Facturacion")));
        haciaFacturacionCongelado.Should().ContainKey(
            new Ubicacion("Application.Dashboard.Queries.ObtenerCuadroDireccionQueryHandler", "Facturacion: Application.Dashboard.ICierresCuadroDireccion"),
            "la arista Outbound -> Facturación del cierre está congelada");

        var haciaOutboundCongelado = ListaCongelada.Leer(File.ReadAllText(ListaCongelada.RutaDeLista("Modulos-Facturacion-a-fuera-del-Nucleo")));
        haciaOutboundCongelado.Should().ContainKey(
            new Ubicacion("Application.Dashboard.FotoCierreMensual", "Outbound: Application.Dashboard.Queries.MedicionCuadroDireccion"),
            "la arista Facturación -> Outbound del cierre está congelada");
    }

    [Fact]
    public void Modo_informe_cifras_por_par_de_modulos()
    {
        var medicion = MedicionDeSrc.Valor.Aristas;
        var sb = new StringBuilder(medicion.Informe(ConfiguracionDeCompilacion()));
        sb.Append($"Tokens del IL sin resolver (referencias a ensamblados opcionales, no vistas): {ReferenciasIl.TokensSinResolver}; opcodes desconocidos: {ReferenciasIl.OpcodesDesconocidos}\n");

        foreach (var lista in Listas)
        {
            foreach (var (origen, destino) in lista.Pares)
            {
                sb.Append($"\n== {origen} -> {destino}: tipo de origen -> tipos de destino ==\n");
                foreach (var (tipo, destinos) in medicion.De(origen, destino))
                    sb.Append($"  {tipo} -> {string.Join(", ", destinos)}\n");
            }
        }

        salida.WriteLine(sb.ToString());
        var directorio = Environment.GetEnvironmentVariable("HYDRA_TRINQUETES_VOLCAR");
        if (!string.IsNullOrWhiteSpace(directorio))
        {
            Directory.CreateDirectory(directorio);
            File.WriteAllText(Path.Combine(directorio, "Modulos-aristas-informe.txt"), sb.ToString());
        }

        sb.Length.Should().BeGreaterThan(0);
    }

    // ---- Controles positivos: tipos sintéticos con una arista prohibida de cada dirección. ----

    /// <summary>
    /// El registro real más los namespaces sintéticos de <c>TiposSinteticosDeAristas</c>. Los tipos reales se miden con el
    /// registro real; los sintéticos, con este.
    /// </summary>
    private static RegistroDeModulos RegistroDeControl() => RegistroDeModulos.Leer(
        File.ReadAllText(RegistroDeModulos.RutaVersionada()) + """

            area Application.ControlDeAristasNucleo = Nucleo
            area Application.ControlDeAristasOutbound = Outbound
            area Application.ControlDeAristasInbound = Inbound
            area Application.ControlDeAristasFacturacion = Facturacion
            """);

    private static IReadOnlyList<Type> TiposSinteticos() =>
        typeof(DependenciasEntreModulosTests).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("CaeManager.Application.ControlDeAristas", StringComparison.Ordinal) == true)
            .ToList();

    private static AristasEntreModulos MedirSinteticos() => AristasEntreModulos.Medir(RegistroDeControl(), TiposSinteticos());

    [Fact]
    public void Control_cada_arista_prohibida_sintetica_se_mide_donde_esta_escondida()
    {
        var m = MedirSinteticos();

        m.OrigenesSinModulo.Should().BeEmpty();
        m.DestinosSinModulo.Should().BeEmpty();
        m.De(Modulo.Nucleo, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasNucleo.OrigenNucleoHaciaOutbound")
            .WhoseValue.Should().Equal("Application.ControlDeAristasOutbound.DestinoOutbound");
        m.De(Modulo.Nucleo, Modulo.Inbound).Should().ContainKey("Application.ControlDeAristasNucleo.OrigenNucleoHaciaInbound");
        m.De(Modulo.Nucleo, Modulo.Facturacion).Should().ContainKey("Application.ControlDeAristasNucleo.OrigenNucleoHaciaFacturacion");
        m.De(Modulo.Outbound, Modulo.Inbound).Should().ContainKey("Application.ControlDeAristasOutbound.OrigenOutboundHaciaInbound");
        m.De(Modulo.Outbound, Modulo.Facturacion).Should().ContainKey("Application.ControlDeAristasOutbound.OrigenOutboundHaciaFacturacion");
        m.De(Modulo.Inbound, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasInbound.OrigenInboundHaciaOutbound");
        m.De(Modulo.Facturacion, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasFacturacion.OrigenFacturacionHaciaOutbound");
        m.De(Modulo.Facturacion, Modulo.Inbound).Should().ContainKey("Application.ControlDeAristasFacturacion.OrigenFacturacionHaciaInbound");
        m.De(Modulo.Inbound, Modulo.Facturacion).Should().ContainKey("Application.ControlDeAristasInbound.OrigenInboundHaciaFacturacion");
    }

    /// <summary>Cada dirección prohibida tiene su propio origen sintético: un par sin control no se vigila de verdad.</summary>
    [Fact]
    public void Control_cada_direccion_prohibida_tiene_un_origen_sintetico_medido()
    {
        var m = MedirSinteticos();

        foreach (var (origen, destino) in ParesProhibidos)
            m.De(origen, destino).Should().NotBeEmpty($"el control sintético {origen} -> {destino} tiene que existir y medirse");
    }

    /// <summary>
    /// Lo que antes se escapaba al lector: el tipo de un <c>catch</c>, la restricción de un parámetro genérico, un
    /// <c>typeof</c> dentro de un array de argumentos de atributo y el cuerpo de un método asíncrono (la máquina de estado).
    /// Cada uno es el único rastro de su arista en el tipo sintético.
    /// </summary>
    [Fact]
    public void Control_el_lector_ve_catch_restricciones_atributos_y_metodos_asincronos()
    {
        var m = MedirSinteticos();

        m.De(Modulo.Inbound, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasInbound.OrigenInboundHaciaOutboundPorCatch")
            .WhoseValue.Should().Contain("Application.ControlDeAristasOutbound.ExcepcionDeOutbound");
        m.De(Modulo.Nucleo, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasNucleo.OrigenNucleoPorRestriccion");
        m.De(Modulo.Facturacion, Modulo.Inbound).Should().ContainKey("Application.ControlDeAristasFacturacion.OrigenFacturacionHaciaInboundPorAtributo");
        m.De(Modulo.Facturacion, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasFacturacion.OrigenFacturacionHaciaOutboundAsincrono");

        // Cada rastro de abajo es el único de su arista: el atributo es del Núcleo y el tipo Outbound solo está en el argumento.
        m.De(Modulo.Nucleo, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasNucleo.OrigenNucleoPorRestriccionDeMetodo");
        m.De(Modulo.Inbound, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasInbound.OrigenInboundHaciaOutboundPorAtributoDeParametro");
        m.De(Modulo.Inbound, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasInbound.OrigenInboundHaciaOutboundPorAtributoDeRetorno");
        m.De(Modulo.Facturacion, Modulo.Outbound).Should().ContainKey("Application.ControlDeAristasFacturacion.OrigenFacturacionHaciaOutboundPorEnumDeAtributo");
    }

    /// <summary>
    /// Un opcode que el lector no conoce trunca el método que lo contiene y un token que no se resuelve (un ensamblado
    /// opcional) esconde lo que ese token nombra: ninguno de los dos se tolera, y el informe da las cifras.
    /// </summary>
    [Fact]
    public void El_lector_de_IL_no_pierde_opcodes_ni_tokens()
    {
        _ = MedicionDeSrc.Valor.Aristas;

        ReferenciasIl.OpcodesDesconocidos.Should().Be(0, "un opcode desconocido trunca el método y las aristas que viniera después se pierden");
        ReferenciasIl.TokensSinResolver.Should().Be(0, "un token sin resolver esconde un tipo referenciado: la arista que lo use no se mide");
    }

    [Fact]
    public void Control_lo_permitido_no_es_una_arista_prohibida()
    {
        var m = MedirSinteticos();

        m.De(Modulo.Outbound, Modulo.Nucleo).Should().ContainKey("Application.ControlDeAristasOutbound.OrigenOutboundHaciaNucleo");
        m.De(Modulo.Inbound, Modulo.Nucleo).Should().ContainKey("Application.ControlDeAristasInbound.OrigenInboundHaciaNucleo");
        m.De(Modulo.Facturacion, Modulo.Nucleo).Should().ContainKey("Application.ControlDeAristasFacturacion.OrigenFacturacionHaciaNucleo");
        foreach (var lista in Listas)
            foreach (var (origen, destino) in lista.Pares)
                destino.Should().NotBe(Modulo.Nucleo, $"{origen} -> Núcleo es una dirección permitida: se mide, pero no se lista");

        // Los tipos limpios (solo su propio módulo) no tienen ninguna arista.
        m.De(Modulo.Nucleo, Modulo.Nucleo).Should().BeEmpty("dentro de un módulo no hay aristas");
        m.De(Modulo.Nucleo, Modulo.Outbound).Should().NotContainKey("Application.ControlDeAristasNucleo.OrigenNucleoLimpio");
    }

    [Theory]
    [MemberData(nameof(NombresDeListas))]
    public void Control_una_arista_prohibida_nueva_da_rojo_contra_la_lista_real(string nombre)
    {
        var lista = Listas.Single(l => l.Nombre == nombre);
        var conSinteticos = MedicionDeSrc.Valor.Aristas.Con(MedirSinteticos());

        var fallo = ListaCongelada.Evaluar(nombre, ListaCongelada.RutaDeLista(nombre), Medido(conSinteticos, lista), lista.Guia,
            directorioDeVolcado: null, File.Exists, File.ReadAllText);

        fallo.Should().NotBeNull($"una arista prohibida nueva en {nombre} tiene que poner el test en rojo");
        fallo!.Split('\n').Where(l => l.TrimStart().StartsWith("NUEVA", StringComparison.Ordinal) && !l.Contains("ControlDeAristas"))
            .Should().BeEmpty("lo único nuevo respecto a la lista real son los tipos sintéticos");
        fallo.Should().Contain("NUEVA").And.Contain("ControlDeAristas");
    }

    [Fact]
    public void Control_una_arista_que_desaparece_de_la_medida_obliga_a_borrar_su_linea()
    {
        // Quitar de la medida real la primera arista de cada lista deja exactamente una línea obsoleta, y solo esa.
        foreach (var lista in Listas)
        {
            var medido = Medido(MedicionDeSrc.Valor.Aristas, lista);
            var listado = ListaCongelada.Leer(File.ReadAllText(ListaCongelada.RutaDeLista(lista.Nombre)));
            if (medido.Count == 0)
                continue; // lista ya vaciada: no hay arista que quitar (el test de la lista exige que siga vacía)

            var quitada = medido.Keys.OrderBy(k => k.Lugar, StringComparer.Ordinal).ThenBy(k => k.Simbolo, StringComparer.Ordinal).First();
            medido.Remove(quitada);

            ListaCongelada.Desvios(medido, listado).Should().ContainSingle($"{lista.Nombre}: quitar una arista deja una línea obsoleta")
                .Which.Should().StartWith("OBSOLETA").And.Contain(quitada.Lugar);
        }
    }

    // ---- Soporte ----

    private static Dictionary<Ubicacion, int> Medido(AristasEntreModulos medicion, Lista lista)
    {
        var resultado = new Dictionary<Ubicacion, int>();
        foreach (var (origen, destino) in lista.Pares)
        {
            foreach (var (tipo, destinos) in medicion.De(origen, destino))
            {
                if (lista.Grano == Grano.Tipo)
                {
                    foreach (var d in destinos)
                        resultado[new Ubicacion(tipo, $"{destino}: {d}")] = 1;
                }
                else
                {
                    resultado[new Ubicacion(tipo, $"-> {destino}")] = destinos.Count;
                }
            }
        }

        return resultado;
    }

    private static string ConfiguracionDeCompilacion() =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}

/// <summary>
/// La medida de <c>src</c> con el registro versionado, calculada una vez por ejecución: leer el IL de cinco ensamblados
/// es lo caro, y la comparten las listas, el informe y la regla de Facturación.
/// </summary>
internal sealed class MedicionDeSrc
{
    private static readonly Lazy<MedicionDeSrc> Perezoso = new(() =>
    {
        var registro = RegistroDeModulos.Versionado();
        return new MedicionDeSrc(registro, AristasEntreModulos.Medir(registro, AristasEntreModulos.TiposDeSrc(registro)));
    });

    private MedicionDeSrc(RegistroDeModulos registro, AristasEntreModulos aristas)
    {
        Registro = registro;
        Aristas = aristas;
    }

    public static MedicionDeSrc Valor => Perezoso.Value;

    public RegistroDeModulos Registro { get; }

    public AristasEntreModulos Aristas { get; }
}
