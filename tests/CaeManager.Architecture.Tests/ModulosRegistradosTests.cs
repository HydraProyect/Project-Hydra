using System.Reflection;
using System.Text;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using Xunit.Abstractions;
using AreaDeControl = global::CaeManager.Domain.AreaSinRegistrarDeControl;
using ConfiguracionesDeControl = global::CaeManager.Infrastructure.Persistence.Configurations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// ADR-013, paso M0a (§ 4.3 test 1; § 4.7 mecanismo 1): el registro de módulos es <b>cerrado</b>. Todo tipo de los cinco
/// ensamblados de <c>src</c> tiene exactamente un módulo (Núcleo, Outbound, Inbound, Facturación o Composición), sin
/// mover ningún fichero. Un tipo nuevo en un área sin registrar da rojo con su nombre: es lo que hace sensible al
/// instrumento, y lo que obliga a decidir el módulo de cada feature nueva en el mismo PR.
///
/// <para>
/// El dato está en <c>Modulos/Modulos-registro.txt</c>; la mecánica, en <see cref="RegistroDeModulos"/>. Este test
/// <b>no</b> mira dependencias entre módulos: eso es M0b, con el IL. Tampoco ve lo que no es un tipo (SQL en cadenas,
/// columnas <c>Guid</c> sin FK, literales que nombran features, <c>cref</c> de documentación; ADR-013 § 4.3).
/// </para>
/// </summary>
public class ModulosRegistradosTests(ITestOutputHelper salida)
{
    /// <summary>
    /// Las entradas Inbound que viven fuera de <c>*.Inbound.*</c>: las carpetas y la excepción que ya existían al crear
    /// el registro (ADR-013 § 2.2). M1 las movió bajo <c>*.Inbound.*</c> y la lista quedó vacía. Lista cerrada: lo
    /// nuevo de Inbound nace bajo <c>*.Inbound.*</c> (§ 4.7), así que una línea Inbound fuera de ahí da rojo aunque el
    /// tipo quede clasificado.
    /// </summary>
    private static readonly HashSet<string> InboundHeredado = new(StringComparer.Ordinal);

    /// <summary>
    /// Anclas de intención: tipos cuyo módulo fija ADR-013 § 3 o D-M1, y una de cada regla (área, excepción, derivación
    /// de configuración y de repositorio, composición). Si una línea del registro cambia su sentido, cae aquí, no solo
    /// en el recuento.
    /// </summary>
    public static TheoryData<string, Modulo> Anclas => new()
    {
        { "CaeManager.Domain.Documentos.Documento", Modulo.Nucleo },
        { "CaeManager.Domain.Documentos.TipoDocumento", Modulo.Nucleo },
        { "CaeManager.Domain.Documentos.EstadoAcreditacion", Modulo.Outbound },
        // Del Núcleo, compartida por Outbound y la evidencia Inbound (ADR-013 § 4.3).
        { "CaeManager.Domain.Documentos.CausaRechazoAcreditacion", Modulo.Nucleo },
        { "CaeManager.Domain.Inbound.ContratosAcceso.ContratoAcceso", Modulo.Inbound },
        { "CaeManager.Domain.Inbound.EvidenciasInbound.EvidenciaInbound", Modulo.Inbound },
        { "CaeManager.Domain.Facturacion.TarifaCliente", Modulo.Facturacion },
        { "CaeManager.Domain.Dashboard.TarifaOperadorCae", Modulo.Facturacion },
        { "CaeManager.Domain.Dashboard.CierreMensualTenantBeneficiario", Modulo.Facturacion },
        { "CaeManager.Domain.Operaciones.AsignacionOperacion", Modulo.Nucleo },
        { "CaeManager.Domain.Operaciones.ServicioCae", Modulo.Nucleo },
        { "CaeManager.Application.Common.IAlcanceDatosService", Modulo.Nucleo },
        { "CaeManager.Infrastructure.Inbound.Autorizacion.LectorDocumentosInboundEnBase", Modulo.Inbound },
        { "CaeManager.Infrastructure.Persistence.CaeManagerDbContext", Modulo.Composicion },
        { "CaeManager.Infrastructure.Persistence.Configurations.TarifaClienteConfiguration", Modulo.Facturacion },
        { "CaeManager.Infrastructure.Persistence.Configurations.TipoDocumentoConfiguration", Modulo.Nucleo },
        { "CaeManager.Infrastructure.Inbound.Persistence.ContratoAccesoRepository", Modulo.Inbound },
        { "CaeManager.Infrastructure.Persistence.Repositories.AcreditacionDocumentoPlataformaRepository", Modulo.Outbound },
        { "CaeManager.Infrastructure.Persistence.Interceptors.TenantRlsConnectionInterceptor", Modulo.Nucleo },
        { "Program", Modulo.Composicion },
        // Corte tipo a tipo dentro de un área (N-1 y cierre mensual de D-M1): borrar una de estas excepciones devolvería
        // el tipo en silencio al módulo de su área. No todas las excepciones tienen ancla (las de Tarifas de Operador CAE,
        // Documentos, Integraciones y Web no): un ancla por corte, no por línea.
        { "CaeManager.Application.Clientes.IBloqueoCarteraUsuario", Modulo.Nucleo },
        { "CaeManager.Application.Clientes.IDirectorioDestinosCartera", Modulo.Nucleo },
        { "CaeManager.Application.Clientes.ReglaDestinoCarteraCliente", Modulo.Outbound },
        { "CaeManager.Application.Dashboard.FotoCierreMensual", Modulo.Facturacion },
        { "CaeManager.Application.Dashboard.ICierresCuadroDireccion", Modulo.Facturacion },
        { "CaeManager.Application.Dashboard.ReabrirMesCuadroDireccionCommand", Modulo.Facturacion },
        { "CaeManager.Infrastructure.Dashboard.CierreMensualCuadroDireccionHostedService", Modulo.Facturacion },
    };

    [Fact]
    public void Todo_tipo_de_src_tiene_modulo()
    {
        var registro = RegistroDeModulos.Versionado();
        var tipos = TiposDeSrc(registro);

        tipos.Should().HaveCountGreaterThan(2000, "control del instrumento: un vacío no es un registro completo");
        foreach (var ensamblado in RegistroDeModulos.EnsambladosDeSrc())
            tipos.Should().Contain(t => t.Assembly == ensamblado, $"control del instrumento: tiene que mirar {ensamblado.GetName().Name}");

        var sinModulo = TiposSinModulo(registro, tipos);
        sinModulo.Should().BeEmpty(
            "todo tipo de src tiene módulo (ADR-013 § 4.3). Añade en Modulos/Modulos-registro.txt el área de su carpeta " +
            "o una excepción por tipo; lo nuevo de Inbound nace bajo *.Inbound.* (§ 4.7). Sin módulo:\n  " +
            string.Join("\n  ", sinModulo.Take(60)));

        Resumir(registro, tipos);
    }

    [Fact]
    public void Toda_entrada_del_registro_clasifica_algun_tipo()
    {
        var registro = RegistroDeModulos.Versionado();
        var tipos = TiposDeSrc(registro);
        var usadas = EntradasUsadas(registro, tipos);

        var obsoletas = registro.Entradas.Where(e => !usadas.Contains(e) && !e.Reservada).Select(e => e.ToString()).ToList();
        obsoletas.Should().BeEmpty(
            "una entrada que no clasifica ningún tipo es obsoleta o está mal escrita: bórrala o corrige la clave. Obsoletas:\n  " +
            string.Join("\n  ", obsoletas));

        // Qué se puede reservar lo impone la lectura (RegistroDeModulos.Leer, con su control de línea inválida).
    }

    /// <summary>
    /// El registro solo es cerrado si mira todos los proyectos de <c>src</c>: un proyecto nuevo fuera de
    /// <see cref="RegistroDeModulos.EnsambladosDeSrc"/> escaparía sin dar rojo.
    /// </summary>
    [Fact]
    public void El_registro_mira_todos_los_proyectos_de_src()
    {
        var proyectos = Directory.EnumerateFiles(Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "src"), "*.csproj",
                SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToList();

        proyectos.Should().HaveCountGreaterThan(1, "control del instrumento: tiene que encontrar los proyectos de src");
        RegistroDeModulos.EnsambladosDeSrc().Select(a => a.GetName().Name).Order(StringComparer.Ordinal)
            .Should().Equal(proyectos, "cada proyecto de src se añade a RegistroDeModulos.EnsambladosDeSrc y al registro");
    }

    /// <summary>
    /// <see cref="ReflexionArquitecturaHelper.TiposDe"/> se traga <see cref="ReflectionTypeLoadException"/> y devuelve
    /// los tipos que sí cargan: un tipo que no carga quedaría fuera del registro sin dar rojo. Aquí se exige que no haya
    /// ninguno en los cinco ensamblados.
    /// </summary>
    [Fact]
    public void Todos_los_tipos_de_src_cargan()
    {
        foreach (var ensamblado in RegistroDeModulos.EnsambladosDeSrc())
        {
            var cargar = () => ensamblado.GetTypes();
            cargar.Should().NotThrow<ReflectionTypeLoadException>(
                $"un tipo de {ensamblado.GetName().Name} que no carga escaparía al registro de módulos");
        }
    }

    [Fact]
    public void Ninguna_excepcion_repite_el_modulo_que_ya_tendria()
    {
        var registro = RegistroDeModulos.Versionado();
        var tipos = TiposDeSrc(registro);

        var redundantes = registro.Entradas
            .Where(e => e.Clase == "excepcion")
            .Select(e => (Entrada: e, Tipos: tipos.Where(t => registro.Clasificar(t)?.Entrada == e).ToList()))
            .Where(x => x.Tipos.Count > 0
                        && x.Tipos.All(t => registro.ClasificarSinExcepciones(t)?.Modulo == x.Entrada.Modulo)
                        // Una excepción más honda puede devolver al módulo del área lo que otra excepción más ancha saca.
                        && !registro.Entradas.Any(o => o.Clase == "excepcion" && o != x.Entrada
                                                       && x.Entrada.Clave.StartsWith(o.Clave + ".", StringComparison.Ordinal)))
            .Select(x => x.Entrada.ToString())
            .ToList();

        redundantes.Should().BeEmpty("una excepción que da el mismo módulo que su área o su derivación sobra:\n  " +
                                     string.Join("\n  ", redundantes));
    }

    [Fact]
    public void Las_entradas_Inbound_fuera_de_Inbound_son_solo_las_heredadas()
    {
        var nuevas = RegistroDeModulos.Versionado().Entradas
            .Where(e => e.Modulo == Modulo.Inbound && !e.Clave.Split('.').Contains("Inbound") && !InboundHeredado.Contains(e.Clave))
            .Select(e => e.ToString())
            .ToList();

        nuevas.Should().BeEmpty("lo nuevo de Inbound nace bajo *.Inbound.* (ADR-013 § 4.7), no en una carpeta nueva de primer nivel:\n  " +
                                string.Join("\n  ", nuevas));
    }

    [Theory]
    [MemberData(nameof(Anclas))]
    public void Las_anclas_tienen_el_modulo_que_fija_ADR_013(string nombreCompleto, Modulo esperado)
    {
        var tipo = RegistroDeModulos.EnsambladosDeSrc()
            .Select(a => a.GetType(nombreCompleto, throwOnError: false))
            .SingleOrDefault(t => t is not null);
        tipo.Should().NotBeNull($"el ancla {nombreCompleto} tiene que existir; si se movió, actualiza el ancla y el registro");

        var clasificacion = RegistroDeModulos.Versionado().Clasificar(tipo!);
        clasificacion.Should().NotBeNull($"el ancla {nombreCompleto} tiene que tener módulo");
        clasificacion!.Value.Modulo.Should().Be(esperado);
    }

    // ---- Controles positivos del instrumento: tipos sintéticos, nunca una entrada del propio registro. ----

    [Fact]
    public void Control_un_tipo_sintetico_en_un_area_sin_registrar_da_rojo_con_su_nombre()
    {
        var registro = RegistroDeModulos.Versionado();
        var tipos = TiposDeSrc(registro).Append(typeof(AreaDeControl.TipoSinModuloDeControl)).ToList();

        TiposSinModulo(registro, tipos).Should().Contain(
            "CaeManager.Domain.AreaSinRegistrarDeControl.TipoSinModuloDeControl (sin área ni excepción)");
    }

    [Fact]
    public void Control_un_tipo_sintetico_fuera_de_CaeManager_no_se_escapa()
    {
        var registro = RegistroDeModulos.Versionado();

        registro.EsAjeno(typeof(Sintetico.FueraDeTalveg.TipoAjenoDeControl)).Should().BeFalse();
        TiposSinModulo(registro, [typeof(Sintetico.FueraDeTalveg.TipoAjenoDeControl)]).Should().ContainSingle();
    }

    [Fact]
    public void Control_las_reglas_de_precedencia_con_un_registro_sintetico()
    {
        var registro = RegistroDeModulos.Leer("""
            area Domain.Facturacion = Facturacion
            area Domain.Empresas = Nucleo
            area Domain.AreaSinRegistrarDeControl = Nucleo provisional # control
            excepcion Domain.AreaSinRegistrarDeControl.TipoSinModuloDeControl = Outbound
            derivar Infrastructure.Persistence.Configurations
            """);

        registro.Clasificar(typeof(AreaDeControl.OtroTipoDeControl))!.Value.Modulo.Should().Be(Modulo.Nucleo);
        registro.Clasificar(typeof(AreaDeControl.TipoSinModuloDeControl))!.Value.Modulo.Should().Be(Modulo.Outbound);
        registro.Clasificar(typeof(AreaDeControl.OtroTipoDeControl.Anidado))!.Value.Modulo
            .Should().Be(Modulo.Nucleo, "un anidado sigue a su contenedor");

        // Derivación (B-1): la configuración sigue a su entidad; dos entidades de módulos distintos la dejan sin módulo.
        registro.Clasificar(typeof(ConfiguracionesDeControl.ConfiguracionDeControl))!.Value.Modulo
            .Should().Be(Modulo.Facturacion);
        registro.Clasificar(typeof(ConfiguracionesDeControl.ConfiguracionMixtaDeControl))
            .Should().BeNull("una configuración de dos módulos necesita una excepción explícita");
        registro.Clasificar(typeof(ConfiguracionesDeControl.AuxiliarSinEntidadDeControl))
            .Should().BeNull("dentro de un namespace derivado, lo que no configura ni implementa nada no hereda un área");

        // Una excepción gana a la derivación: resuelve la configuración mixta sin tocar el resto del namespace.
        var conExcepcion = RegistroDeModulos.Leer("""
            area Domain.Facturacion = Facturacion
            area Domain.Empresas = Nucleo
            excepcion Infrastructure.Persistence.Configurations.ConfiguracionMixtaDeControl = Outbound
            excepcion Infrastructure.Persistence.Configurations.ConfiguracionDeControl = Nucleo
            derivar Infrastructure.Persistence.Configurations
            """);
        conExcepcion.Clasificar(typeof(ConfiguracionesDeControl.ConfiguracionMixtaDeControl))!.Value.Modulo
            .Should().Be(Modulo.Outbound);
        conExcepcion.Clasificar(typeof(ConfiguracionesDeControl.ConfiguracionDeControl))!.Value.Modulo
            .Should().Be(Modulo.Nucleo, "la excepción gana aunque la derivación diera Facturacion");
        conExcepcion.EstaEnDerivacion(typeof(ConfiguracionesDeControl.AuxiliarSinEntidadDeControl)).Should().BeTrue();
        conExcepcion.EstaEnDerivacion(typeof(AreaDeControl.OtroTipoDeControl)).Should().BeFalse();
    }

    [Theory]
    [InlineData("area Domain.X Nucleo", "mal formada")]
    [InlineData("area Domain.X = Satelite", "mal formada")]
    [InlineData("area Domain.X = Nucleo provisional", "motivo")]
    [InlineData("area Domain.X.Y = Nucleo", "primer nivel")]
    [InlineData("area CaeManager.Domain.X = Nucleo", "sin el prefijo")]
    [InlineData("excepcion Domain.X.T = Nucleo reservada", "reservada")]
    [InlineData("area Domain.Inbound = Outbound reservada", "nombre de su módulo")]
    [InlineData("area Domain.Nucleo = Nucleo reservada", "nombre de su módulo")]
    [InlineData("area Domain.X = Nucleo\narea Domain.X = Outbound", "repetida")]
    public void Control_una_linea_invalida_del_registro_no_se_ignora(string texto, string mensaje)
    {
        var leer = () => RegistroDeModulos.Leer(texto);
        leer.Should().Throw<FormatException>().Where(e => e.Message.Contains(mensaje));
    }

    // ---- Soporte ----

    private static IReadOnlyList<Type> TiposDeSrc(RegistroDeModulos registro) =>
        RegistroDeModulos.TiposAClasificar(RegistroDeModulos.EnsambladosDeSrc()).Where(t => !registro.EsAjeno(t)).ToList();

    private static List<string> TiposSinModulo(RegistroDeModulos registro, IEnumerable<Type> tipos) =>
        RegistroDeModulos.TiposAClasificar(tipos)
            .Where(t => !registro.EsAjeno(t) && registro.Clasificar(t) is null)
            .Select(t => t.FullName + (registro.EstaEnDerivacion(t) && RegistroDeModulos.FuentesDeDerivacion(t).Count > 1
                ? " (la derivación da varios módulos: añade una excepción)"
                : " (sin área ni excepción)"))
            .ToList();

    private static HashSet<EntradaDeRegistro> EntradasUsadas(RegistroDeModulos registro, IReadOnlyList<Type> tipos)
    {
        var usadas = new HashSet<EntradaDeRegistro>();
        foreach (var tipo in tipos)
        {
            if (registro.Clasificar(tipo) is { Entrada: { } entrada })
                usadas.Add(entrada);
        }

        var todos = RegistroDeModulos.TiposAClasificar(RegistroDeModulos.EnsambladosDeSrc());
        foreach (var entrada in registro.Entradas)
        {
            var clave = entrada.Clave;
            var usa = entrada.Clase switch
            {
                "derivar" => tipos.Any(t => EnNamespace(RegistroDeModulos.NombreRelativo(t), clave) && registro.Clasificar(t)?.Entrada is null),
                "ajeno" => todos.Any(t => EnNamespace(RegistroDeModulos.NamespaceDe(t), clave) && registro.EsAjeno(t)),
                _ => false,
            };
            if (usa)
                usadas.Add(entrada);
        }

        return usadas;
    }

    private static bool EnNamespace(string nombre, string prefijo) =>
        nombre == prefijo || nombre.StartsWith(prefijo + ".", StringComparison.Ordinal);

    /// <summary>Tipos por módulo y por clase de regla, al log del test y, con HYDRA_TRINQUETES_VOLCAR, a un fichero.</summary>
    private void Resumir(RegistroDeModulos registro, IReadOnlyList<Type> tipos)
    {
        var clasificados = tipos.Select(t => (Tipo: t, C: registro.Clasificar(t)!.Value)).ToList();
        var sb = new StringBuilder();
        sb.Append("tipos clasificados: ").Append(clasificados.Count).Append('\n');
        foreach (var grupo in clasificados.GroupBy(x => x.C.Modulo).OrderBy(g => g.Key))
            sb.Append($"  {grupo.Key}: {grupo.Count()}\n");
        foreach (var grupo in clasificados.GroupBy(x => x.C.Entrada?.Clase ?? "derivado").OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.Append($"  por {grupo.Key}: {grupo.Count()}\n");
        var entradas = registro.Entradas;
        sb.Append($"entradas: {entradas.Count} (area {entradas.Count(e => e.Clase == "area")}, excepcion {entradas.Count(e => e.Clase == "excepcion")}, " +
                  $"derivar {entradas.Count(e => e.Clase == "derivar")}, ajeno {entradas.Count(e => e.Clase == "ajeno")}; " +
                  $"provisionales {entradas.Count(e => e.Provisional)}, reservadas {entradas.Count(e => e.Reservada)})\n");

        salida.WriteLine(sb.ToString());
        var directorio = Environment.GetEnvironmentVariable("HYDRA_TRINQUETES_VOLCAR");
        if (string.IsNullOrWhiteSpace(directorio))
            return;
        Directory.CreateDirectory(directorio);
        File.WriteAllText(Path.Combine(directorio, "Modulos-resumen.txt"), sb.ToString());
        File.WriteAllLines(Path.Combine(directorio, "Modulos-por-tipo.txt"),
            clasificados.Select(x => $"{x.Tipo.FullName} = {x.C.Modulo} :: {x.C.Regla}"));
    }
}
