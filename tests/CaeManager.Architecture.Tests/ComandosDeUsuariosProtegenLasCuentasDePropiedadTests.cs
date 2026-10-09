using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using CaeManager.Application.Usuarios.Commands.AsumirPrincipalDeOperacion;
using CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;
using CaeManager.Application.Usuarios.Commands.CrearUsuario;
using CaeManager.Application.Usuarios.Commands.DesignarGestorCaePrincipal;
using CaeManager.Application.Usuarios.Commands.ElegirAvatarPropio;
using CaeManager.Application.Usuarios.Commands.GenerarCodigosRecuperacion;
using CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Todo comando de <c>Application/Usuarios/Commands</c> protege las cuentas con rol de Propiedad
/// o está exento con motivo</b> (decisión D-8, 2026-10-08; primer acto excluido del Encargo de
/// administración, § 9.5.1 del diseño).
///
/// <para>
/// Quien actúa en un Tenant que no es su Tenant de origen —con rol de cartera o con el rol elevado
/// por un encargo— no concede Administrador ni Dirección CAE
/// (<see cref="RolesReservadosAlTenantDeOrigen"/>) ni actúa sobre una cuenta que ya tiene uno de
/// esos roles (<see cref="CuentasConRolDePropiedad"/>). La regla vive en cada handler, así que un
/// comando nuevo que edite, active, elimine o cambie una cuenta nace sin ella si nadie se acuerda.
/// Este trinquete obliga a que cada comando del área llame a la comprobación o se declare exento.
/// </para>
///
/// <para>
/// <b>Lo que observa</b>: por reflexión, cada petición de MediatR cuyo espacio de nombres cuelga de
/// <c>CaeManager.Application.Usuarios.Commands</c>; y por texto, sin comentarios, los <c>.cs</c> de
/// la carpeta de ese comando, donde busca la llamada a la comprobación que le toca.
/// <b>Lo que NO observa</b> (huecos declarados): que el resultado de la llamada se respete (lo
/// prueban los tests de cada handler en Application.Tests); un comando que actúe sobre cuentas
/// desde otra área de Application; y una comprobación movida a un ayudante fuera de la carpeta del
/// comando, que pondría este test en rojo aunque la regla siguiera cumpliéndose.
/// </para>
/// </summary>
public class ComandosDeUsuariosProtegenLasCuentasDePropiedadTests
{
    private const string EspacioDeLosComandos = "CaeManager.Application.Usuarios.Commands.";

    private const string LlamadaSobreElDestino = "CuentasConRolDePropiedad.VerificarDestino(";
    private const string LlamadaSobreElRolConcedido = "RolesReservadosAlTenantDeOrigen.Verificar(";

    /// <summary>
    /// Altas: todavía no hay cuenta destino que mirar. Lo que se comprueba es el rol que se concede.
    /// </summary>
    private static readonly HashSet<Type> Altas = [typeof(CrearUsuarioCommand)];

    private enum Exencion
    {
        /// <summary>El comando solo actúa sobre la cuenta de quien lo envía.</summary>
        Autoservicio,

        /// <summary>
        /// La autoridad se lee en el Tenant de origen del actor, no en el Tenant que opera: fuera de
        /// su organización no hay rol de cartera ni rol elevado que la conceda.
        /// </summary>
        AutoridadEnElTenantDeOrigen,
    }

    /// <summary>
    /// Lista CERRADA de comandos que no llaman a la comprobación, con la clase de exención (que el
    /// test verifica) y el motivo. No es el control positivo del detector: ese va aparte, en
    /// <see cref="El_detector_ve_las_llamadas_reales_y_no_las_de_un_comentario"/>.
    /// </summary>
    private static readonly Dictionary<Type, (Exencion Clase, string Motivo)> Exentos = new()
    {
        [typeof(ElegirAvatarPropioCommand)] = (Exencion.Autoservicio,
            "cada usuario elige el avatar de su propia cuenta; no hay cuenta destino ajena"),
        [typeof(GenerarCodigosRecuperacionCommand)] = (Exencion.Autoservicio,
            "cada usuario genera los códigos de recuperación de su propia cuenta"),
        [typeof(RestablecerSegundoFactorCommand)] = (Exencion.AutoridadEnElTenantDeOrigen,
            "AutorizacionRestablecerSegundoFactor exige que el Tenant de origen del actor sea el Tenant operado (o una "
            + "Sesión Privilegiada de soporte): nadie restablece el segundo factor de otro desde un Tenant ajeno"),
        [typeof(AsignarCarteraGestorCaeCommand)] = (Exencion.AutoridadEnElTenantDeOrigen,
            "AutoridadSobreCarteraDeGestorCae lee el rol del actor en su Tenant de origen y solo alcanza a un Gestor CAE "
            + "de su propio Operador CAE: reparte carteras, no toca la cuenta, y el destino nunca tiene rol de Propiedad"),
        [typeof(DesignarGestorCaePrincipalCommand)] = (Exencion.AutoridadEnElTenantDeOrigen,
            "misma forma de autoridad que la cartera: el rol del actor se lee en su Tenant de origen; marca como principal "
            + "una cartera que ya existe, no toca la cuenta"),
        [typeof(AsumirPrincipalDeOperacionCommand)] = (Exencion.AutoridadEnElTenantDeOrigen,
            "quien asume la cartera principal es el propio actor, y su rol se lee en su Tenant de origen: no hay cuenta "
            + "destino ajena ni rol de cartera o elevado que lo conceda desde el Tenant que opera"),
    };

    private static readonly List<Type> Comandos = typeof(CuentasConRolDePropiedad).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsInterface: false }
                    && typeof(IBaseRequest).IsAssignableFrom(t)
                    && t.FullName!.StartsWith(EspacioDeLosComandos, StringComparison.Ordinal))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void Todo_comando_de_Usuarios_llama_a_la_comprobacion_o_esta_exento_con_motivo()
    {
        Comandos.Should().HaveCountGreaterThanOrEqualTo(11, "control positivo: la reflexión ve los comandos del área");

        var sinProteger = Comandos
            .Where(c => !Exentos.ContainsKey(c))
            .Where(c => !CodigoDe(c).Contains(LlamadaQueLeToca(c), StringComparison.Ordinal))
            .Select(c => c.Name)
            .ToList();

        sinProteger.Should().BeEmpty(
            "un comando de Usuarios que actúa sobre una cuenta destino llama a CuentasConRolDePropiedad.VerificarDestino "
            + "(un alta, a RolesReservadosAlTenantDeOrigen.Verificar): sin eso, quien opera un Tenant ajeno —también por "
            + "Encargo de administración— puede tocar la cuenta de un Administrador o de una Dirección CAE. Si de verdad "
            + "no le aplica, se añade a Exentos con su clase y su motivo");
    }

    [Fact]
    public void Ninguna_exencion_sobra_ni_ha_dejado_de_ser_cierta()
    {
        Exentos.Keys.Should().BeSubsetOf(Comandos, "la lista de exentos nombra comandos que existen en el área");
        Exentos.Keys.Should().NotIntersectWith(Altas);

        foreach (var (comando, (clase, motivo)) in Exentos)
        {
            motivo.Should().NotBeNullOrWhiteSpace();
            var codigo = CodigoDe(comando);

            codigo.Should().NotContain(LlamadaSobreElDestino,
                $"{comando.Name} ya llama a la comprobación: su exención sobra y esconde que está protegido");

            switch (clase)
            {
                case Exencion.Autoservicio:
                    typeof(IComandoDeAutoservicio).IsAssignableFrom(comando).Should().BeTrue(
                        $"{comando.Name} está exento por autoservicio: tiene que seguir marcado como IComandoDeAutoservicio");
                    break;
                case Exencion.AutoridadEnElTenantDeOrigen:
                    codigo.Should().Contain("ObtenerTenantOrigenIdAsync(",
                        $"{comando.Name} está exento porque su autoridad se lee en el Tenant de origen del actor: si deja de "
                        + "leerlo, la exención ya no vale");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(clase));
            }
        }
    }

    /// <summary>
    /// Control positivo, <b>separado de la lista de exentos</b>: comandos concretos que hoy llaman a
    /// cada comprobación tienen que seguir viéndose, y una llamada que solo está en un comentario
    /// no cuenta. Sin esto, un detector que dejara de leer las carpetas daría el mismo verde.
    /// </summary>
    [Fact]
    public void El_detector_ve_las_llamadas_reales_y_no_las_de_un_comentario()
    {
        CodigoDe(typeof(CambiarActivacionUsuarioCommand)).Should().Contain(LlamadaSobreElDestino);
        CodigoDe(typeof(CrearUsuarioCommand)).Should().Contain(LlamadaSobreElRolConcedido);
        CodigoDe(typeof(ElegirAvatarPropioCommand)).Should().NotContain(LlamadaSobreElDestino)
            .And.NotContain(LlamadaSobreElRolConcedido);

        LlamadaQueLeToca(typeof(CrearUsuarioCommand)).Should().Be(LlamadaSobreElRolConcedido);
        LlamadaQueLeToca(typeof(CambiarActivacionUsuarioCommand)).Should().Be(LlamadaSobreElDestino);

        SinComentarios($"// {LlamadaSobreElDestino}roles, origen, tenant);\nvar x = 1;").Should().NotContain(LlamadaSobreElDestino);
        SinComentarios($"/// <see cref=\"X\"/> {LlamadaSobreElDestino}\nvar x = 1;").Should().NotContain(LlamadaSobreElDestino);
        SinComentarios($"/* {LlamadaSobreElDestino} */ var x = 1;").Should().NotContain(LlamadaSobreElDestino);
        SinComentarios($"var r = {LlamadaSobreElDestino}roles, origen, tenant);").Should().Contain(LlamadaSobreElDestino);
    }

    private static string LlamadaQueLeToca(Type comando) =>
        Altas.Contains(comando) ? LlamadaSobreElRolConcedido : LlamadaSobreElDestino;

    private static readonly Regex Comentarios = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string SinComentarios(string codigo) => Comentarios.Replace(codigo, " ");

    /// <summary>El código, sin comentarios, de todos los <c>.cs</c> de la carpeta del comando.</summary>
    private static string CodigoDe(Type comando)
    {
        var carpeta = Path.Combine(
            [Raiz(), "src", "CaeManager.Application", "Usuarios", "Commands", .. comando.Namespace![EspacioDeLosComandos.Length..].Split('.')]);

        Directory.Exists(carpeta).Should().BeTrue(
            $"el espacio de nombres de {comando.FullName} tiene que corresponder a su carpeta; si no, este test no lee su código");

        var ficheros = Directory.EnumerateFiles(carpeta, "*.cs", SearchOption.AllDirectories).ToList();
        ficheros.Should().NotBeEmpty($"la carpeta de {comando.Name} tiene que contener su código");

        return SinComentarios(string.Join("\n", ficheros.Select(File.ReadAllText)));
    }

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName
               ?? throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
