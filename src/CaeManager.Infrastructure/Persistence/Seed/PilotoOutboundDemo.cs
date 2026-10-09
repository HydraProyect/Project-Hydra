using System.Globalization;
using CaeManager.Domain.Centros;
using Microsoft.Extensions.Configuration;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>Qué datos recibe un Tenant propietario del piloto.</summary>
public enum EscenarioPilotoOutbound
{
    /// <summary>Tenant, operación, carteras, Empresa propia y su agenda. Sus datos los añade otro incremento.</summary>
    Esqueleto,

    /// <summary>Las seis condiciones de «todo al día» a la vez: cero filas en Mi trabajo y 100 % en todas las pantallas.</summary>
    TodoAlDia,

    /// <summary>50 % en todas las pantallas (ver <see cref="ConstruccionMitadPilotoOutbound"/>).</summary>
    Mitad,

    /// <summary>Alta reciente: requisitos definidos y nada entregado (ver <see cref="VarianteT4PilotoOutbound"/>).</summary>
    TodoPendiente
}

/// <summary>
/// Cómo se siembra el Tenant «todo pendiente». Existen dos porque las pantallas no
/// cuentan lo mismo: Centros y Empresas cuentan pares exigidos, Inicio y Visión de
/// cartera cuentan documentos existentes y dan 100 cuando no hay ninguno.
/// </summary>
public enum VarianteT4PilotoOutbound
{
    /// <summary>Ningún documento: 0 % en Centros y Empresa; Inicio y Visión de cartera no dan 0 (divergencia declarada).</summary>
    SinDocumentos,

    /// <summary>Un documento Vencido por cada par exigido: 0 % en las cuatro pantallas.</summary>
    DocumentosVencidos
}

/// <param name="Nombre">Nombre completo del Centro de Trabajo, tal como se ve en pantalla.</param>
public sealed record CentroPilotoOutbound(string Nombre, string Localidad);

public sealed record ClienteEmpresarialPilotoOutbound(string RazonSocial, IReadOnlyList<CentroPilotoOutbound> Centros);

/// <param name="Clave">Identificador corto de la matriz (T1..T6): va en los mensajes de la autoverificación, en las etiquetas de correo y en los códigos de Centro.</param>
/// <param name="Nombre">Nombre del Tenant propietario y razón social de su Empresa propia.</param>
/// <param name="EnCarteraDelGestorSegundo">La Gestora CAE primera lleva los seis; el segundo Gestor CAE, solo los marcados.</param>
public sealed record TenantPilotoOutbound(
    string Clave, string Nombre, EscenarioPilotoOutbound Escenario, bool EnCarteraDelGestorSegundo,
    int Trabajadores, IReadOnlyList<ClienteEmpresarialPilotoOutbound> ClientesEmpresariales)
{
    public IReadOnlyList<CentroPilotoOutbound> Centros { get; } = [.. ClientesEmpresariales.SelectMany(c => c.Centros)];
}

/// <summary>Direcciones de las cuentas de la demostración. Las locales son las del arranque en Development; la vía administrativa pasa las de un dominio propio.</summary>
public sealed record CuentasPilotoOutbound(
    string GestoraPrimera, string GestorSegundo, string Coordinadora, string AdministradorOperador, string AdministradorT1)
{
    public static CuentasPilotoOutbound Locales { get; } = new(
        "gestora1.piloto@caemanager.local", "gestor2.piloto@caemanager.local", "coordinadora.piloto@caemanager.local",
        "administrador.piloto@caemanager.local", "administrador.t1.piloto@caemanager.local");

    public IReadOnlyList<string> Todas => [GestoraPrimera, GestorSegundo, Coordinadora, AdministradorOperador, AdministradorT1];
}

/// <summary>
/// A qué dirección escribe «Pedir» en los Tenants del piloto. Con
/// <see cref="Correo"/>, todos los contactos de agenda llevan una variante
/// «+etiqueta» de esa dirección (mismo buzón, y se ve a qué contacto se escribió);
/// sin él, direcciones no entregables bajo <see cref="Dominio"/>.
/// </summary>
public sealed record ContactosPilotoOutbound(string? Correo, string Dominio)
{
    /// <summary>El dominio de la siembra local: «.local» no se enruta, así que nada sale hacia un tercero.</summary>
    public const string DominioPorDefecto = "caemanager.local";

    public static ContactosPilotoOutbound NoEntregables { get; } = new(null, DominioPorDefecto);

    public string DireccionDe(string etiqueta)
    {
        if (Correo is null)
            return $"{etiqueta}@{Dominio}";

        var arroba = Correo.LastIndexOf('@');
        return $"{Correo[..arroba]}+{etiqueta}{Correo[arroba..]}";
    }

    /// <summary>La regla que la autoverificación exige a cada contacto de agenda de los Tenants del piloto.</summary>
    public bool Cumple(string email)
    {
        if (Correo is null)
            return email.EndsWith("@" + Dominio, StringComparison.OrdinalIgnoreCase);

        var arroba = Correo.LastIndexOf('@');
        return email.StartsWith(Correo[..arroba] + "+", StringComparison.OrdinalIgnoreCase)
               && email.EndsWith(Correo[arroba..], StringComparison.OrdinalIgnoreCase);
    }

    internal static ContactosPilotoOutbound Crear(string? correo, string? dominio)
    {
        correo = string.IsNullOrWhiteSpace(correo) ? null : correo.Trim();
        dominio = string.IsNullOrWhiteSpace(dominio) ? DominioPorDefecto : dominio.Trim().TrimStart('@');

        if (correo is not null)
        {
            var arroba = correo.IndexOf('@');
            if (arroba <= 0 || arroba != correo.LastIndexOf('@') || arroba == correo.Length - 1 || correo.Contains('+') || correo.Contains(' '))
                throw new InvalidOperationException(
                    $"{OpcionesPilotoOutbound.ClaveCorreoContactos} no es una dirección utilizable: tiene que ser una sola " +
                    "dirección «usuario@dominio», sin «+» (la siembra añade «+etiqueta» por contacto).");
        }

        return new ContactosPilotoOutbound(correo, dominio);
    }
}

/// <summary>Lo que el arranque lee de <c>DatosPrueba:PilotoOutbound:*</c>.</summary>
public sealed record OpcionesPilotoOutbound(DateOnly FechaDemostracion, ContactosPilotoOutbound Contactos, VarianteT4PilotoOutbound VarianteT4)
{
    public const string Seccion = "DatosPrueba:PilotoOutbound";
    public const string ClaveActivo = Seccion + ":Activo";
    public const string ClaveFechaDemostracion = Seccion + ":FechaDemostracion";
    public const string ClaveCorreoContactos = Seccion + ":CorreoContactos";
    public const string ClaveDominioContactos = Seccion + ":DominioContactos";
    public const string ClaveVarianteT4 = Seccion + ":VarianteT4";

    /// <summary>Días que la demostración puede quedar por delante de hoy: con más, los «Vencido» (hasta D−10) aún no lo estarían.</summary>
    public const int MargenMaximoDias = 9;

    public static bool Activo(IConfiguration configuration) =>
        configuration.GetValue<bool>("DatosPrueba:Activo") && configuration.GetValue<bool>(ClaveActivo);

    /// <summary>Lee y valida las opciones. Lanza <see cref="InvalidOperationException"/> con el nombre de la clave: se llama antes de cualquier escritura.</summary>
    public static OpcionesPilotoOutbound Leer(IConfiguration configuration, DateOnly hoy)
    {
        var textoFecha = configuration[ClaveFechaDemostracion];
        if (string.IsNullOrWhiteSpace(textoFecha))
            throw new InvalidOperationException(
                $"Falta {ClaveFechaDemostracion}: la siembra del piloto ancla todas las fechas al día de la demostración (formato yyyy-MM-dd).");

        if (!DateOnly.TryParseExact(textoFecha.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
            throw new InvalidOperationException(
                $"{ClaveFechaDemostracion} = «{textoFecha}» no es una fecha con formato yyyy-MM-dd.");

        ValidarFecha(fecha, hoy);

        // Por nombre exacto: Enum.TryParse aceptaría también «1», y un número no dice qué variante se pidió.
        var textoVariante = configuration[ClaveVarianteT4];
        var variante = VarianteT4PilotoOutbound.SinDocumentos;
        if (!string.IsNullOrWhiteSpace(textoVariante))
        {
            var nombre = Enum.GetNames<VarianteT4PilotoOutbound>().SingleOrDefault(n => n == textoVariante.Trim())
                ?? throw new InvalidOperationException(
                    $"{ClaveVarianteT4} = «{textoVariante}» no es una variante conocida. Valores: " +
                    $"{string.Join(", ", Enum.GetNames<VarianteT4PilotoOutbound>())}.");
            variante = Enum.Parse<VarianteT4PilotoOutbound>(nombre);
        }

        return new OpcionesPilotoOutbound(
            fecha, ContactosPilotoOutbound.Crear(configuration[ClaveCorreoContactos], configuration[ClaveDominioContactos]), variante);
    }

    public static void ValidarFecha(DateOnly fechaDemostracion, DateOnly hoy)
    {
        if (fechaDemostracion < hoy)
            throw new InvalidOperationException(
                $"{ClaveFechaDemostracion} = {fechaDemostracion:yyyy-MM-dd} es anterior a hoy ({hoy:yyyy-MM-dd}): " +
                "los documentos sembrados como vigentes ya habrían empezado a acercarse a su vencimiento.");

        if (fechaDemostracion > hoy.AddDays(MargenMaximoDias))
            throw new InvalidOperationException(
                $"{ClaveFechaDemostracion} = {fechaDemostracion:yyyy-MM-dd} queda a más de {MargenMaximoDias} días de hoy " +
                $"({hoy:yyyy-MM-dd}): los documentos sembrados como vencidos todavía no lo estarían hoy.");
    }
}

/// <summary>
/// Las fechas de la siembra, todas relativas al día de la demostración (D) y nunca
/// al día en que se siembra: ningún documento cambia de estado entre el ensayo y
/// la demostración. Márgenes de la matriz: Vigente, más de 60 días después;
/// Próximo, 20..25; Urgente, 5..10; Vencido, entre 10 y 90 días antes. Las
/// emisiones son siempre anteriores a D−30, así que nunca son futuras.
/// </summary>
public sealed record FechasPilotoOutbound(DateOnly FechaDemostracion)
{
    public DateOnly Vigente(int i) => FechaDemostracion.AddDays(75 + Positivo(i) % 240);
    public DateOnly Proximo(int i) => FechaDemostracion.AddDays(20 + Positivo(i) % 6);
    public DateOnly Urgente(int i) => FechaDemostracion.AddDays(5 + Positivo(i) % 6);
    public DateOnly Vencido(int i) => FechaDemostracion.AddDays(-(10 + Positivo(i) % 81));

    /// <summary>Emisión de un documento que vence después de D o que no caduca.</summary>
    public DateOnly Emision(int i) => FechaDemostracion.AddDays(-(45 + Positivo(i) % 280));

    /// <summary>Emisión de un documento ya vencido: un año antes de su vencimiento.</summary>
    public static DateOnly EmisionDe(DateOnly vencimiento) => vencimiento.AddYears(-1);

    private static int Positivo(int i) => i < 0 ? -i : i;
}

/// <summary>
/// La construcción del Tenant «mitad», que da 50 % en Centros, Empresa, Inicio y
/// Visión de cartera a la vez. Cada Trabajador tiene Asignación activa a los dos
/// Centros de un mismo Cliente empresarial; el primero exige exactamente
/// {<see cref="TipoVigente"/>, <see cref="TipoVencido"/>} y el segundo exactamente
/// {<see cref="TipoVigente"/>, <see cref="TipoAusente"/>}. Cada Trabajador tiene el
/// primero Vigente, el segundo Vencido y ningún documento del tercero, y ningún
/// documento de otro tipo: así la mitad de los pares exigidos y la mitad de los
/// documentos existentes son conformes.
/// </summary>
public static class ConstruccionMitadPilotoOutbound
{
    public const string TipoVigente = CatalogoPilotoOutbound.AptitudMedica;
    public const string TipoVencido = CatalogoPilotoOutbound.FormacionArt19;
    public const string TipoAusente = CatalogoPilotoOutbound.EntregaEpi;
}

/// <summary>Lo que la autoverificación exige de un Centro: su semáforo y su porcentaje.</summary>
public sealed record EsperadoCentroPilotoOutbound(string Centro, EstadoCentro Estado, int Cumplimiento);

/// <summary>
/// Los valores de la matriz para un Tenant propietario, por pantalla. <c>null</c>
/// en un porcentaje = divergencia declarada: la autoverificación no lo exige y
/// escribe <see cref="Divergencia"/> como advertencia.
/// </summary>
public sealed record EsperadoPilotoOutbound(
    int FilasMiTrabajo, int? CumplimientoInicio, int? CumplimientoVisionCartera, int CumplimientoEmpresa,
    IReadOnlyList<EsperadoCentroPilotoOutbound> Centros, int ParesExigidos, int ParesFaltantes, int Documentos,
    bool TodoAlDia, string? Divergencia = null);

/// <summary>
/// El catálogo declarativo de la siembra del piloto del Servicio TALVEG Outbound:
/// un Operador CAE externo de demostración y seis Tenants propietarios (T1..T6).
/// Todos los nombres propios viven aquí como constantes; el sembrador, la
/// autoverificación y los tests los referencian por constante, nunca por literal.
/// Son nombres inventados: hay que comprobar que no coinciden con una razón social
/// real antes de sembrar fuera de local.
///
/// <para>
/// <b>Para ampliar</b>: un Tenant deja de ser <see cref="EscenarioPilotoOutbound.Esqueleto"/>
/// dándole Clientes empresariales aquí, un constructor en el sembrador y una rama en
/// <see cref="Esperado"/>.
/// </para>
/// </summary>
public static class CatalogoPilotoOutbound
{
    public const string NombreTenantOperador = "Veltria Coordinación CAE";

    public const string NombreTenantT1 = "Instalaciones Ormeval, S.A.";
    public const string NombreTenantT2 = "Climatización Dareno, S.L.";
    public const string NombreTenantT3 = "Mantenimientos Quibera, S.L.";
    public const string NombreTenantT4 = "Montajes Zurela, S.L.";
    public const string NombreTenantT5 = "Inspecciones Brenal, S.L.";
    public const string NombreTenantT6 = "Servicios Técnicos Almadro, S.L.";

    public const string NombreGestoraPrimera = "Laura Benítez Soto";
    public const string NombreGestorSegundo = "Daniel Rubio Marín";
    public const string NombreCoordinadora = "Carmen Lozano Prieto";
    public const string NombreAdministradorOperador = "Jorge Sancho Vera";
    public const string NombreAdministradorT1 = "Beatriz Molina Ferrer";

    public const string AptitudMedica = "Certificado de aptitud médica";
    public const string EntregaEpi = "Entrega de EPI";
    public const string FormacionArt19 = "Formación Art. 19";
    public const string InformacionArt18 = "Información Art. 18";
    public const string DocumentoIdentidad = "Documento de identidad";

    /// <summary>Los cinco tipos de ámbito Trabajador exigidos por defecto. La siembra comprueba que el catálogo del Tenant dice lo mismo antes de escribir.</summary>
    public static IReadOnlyList<string> TiposExigidosDeTrabajador { get; } =
        [AptitudMedica, EntregaEpi, FormacionArt19, InformacionArt18, DocumentoIdentidad];

    /// <summary>El documento de Empresa de T2 que lleva el PDF pesado.</summary>
    public const string TipoDeEmpresaConPdfPesado = "Evaluación de Riesgos Laborales";

    /// <summary>En T2, el Centro con canal de plataforma (todas sus acreditaciones Aceptadas) y con un requisito que bloquea el acceso, cumplido.</summary>
    public const int IndiceCentroConPlataformaT2 = 0;

    public const string TipoBloqueanteCumplidoT2 = AptitudMedica;

    /// <summary>
    /// T4: qué exige cada Centro (entre dos y tres tipos, ninguno bloqueante) y
    /// cuántos Trabajadores tiene. 3×3 + 3×2 + 2×2 = 19 pares.
    /// </summary>
    public static IReadOnlyList<(int Trabajadores, IReadOnlyList<string> Tipos)> RequisitosPorCentroT4 { get; } =
    [
        (3, [AptitudMedica, EntregaEpi, FormacionArt19]),
        (3, [AptitudMedica, EntregaEpi]),
        (2, [AptitudMedica, FormacionArt19])
    ];

    /// <summary>T2: Asignaciones activas (Trabajador, Centro) y las dos dadas de baja. Trece activas × cinco tipos = 65 pares.</summary>
    public static IReadOnlyList<(int Trabajador, int Centro)> AsignacionesActivasT2 { get; } =
    [
        (0, 0), (1, 0), (2, 1), (3, 1), (4, 2), (5, 2), (6, 3), (7, 3), (8, 4), (9, 4),
        (0, 1), (4, 3), (8, 0)
    ];

    public static IReadOnlyList<(int Trabajador, int Centro)> AsignacionesDeBajaT2 { get; } = [(10, 0), (11, 2)];

    public static TenantPilotoOutbound T1 { get; } = new("T1", NombreTenantT1, EscenarioPilotoOutbound.Esqueleto, false, 0, []);

    public static TenantPilotoOutbound T2 { get; } = new("T2", NombreTenantT2, EscenarioPilotoOutbound.TodoAlDia, true, 12,
    [
        new("Alimentaria Sorvena, S.A.", [new("Planta de envasado de Zaragoza", "Zaragoza"), new("Almacén frigorífico de Zaragoza", "Zaragoza")]),
        new("Logística Trebial, S.L.", [new("Centro logístico de Getafe", "Getafe"), new("Plataforma de cruce de Coslada", "Coslada")]),
        new("Química Naldera, S.A.", [new("Planta de formulación de Tarragona", "Tarragona")])
    ]);

    public static TenantPilotoOutbound T3 { get; } = new("T3", NombreTenantT3, EscenarioPilotoOutbound.Mitad, true, 10,
    [
        new("Envases Milcora, S.A.", [new("Fábrica de Burgos", "Burgos"), new("Almacén de expediciones de Burgos", "Burgos")]),
        new("Papelera Urbiate, S.L.", [new("Planta de producción de Tolosa", "Tolosa"), new("Taller de mantenimiento de Tolosa", "Tolosa")])
    ]);

    public static TenantPilotoOutbound T4 { get; } = new("T4", NombreTenantT4, EscenarioPilotoOutbound.TodoPendiente, false, 8,
    [
        new("Aceros Vendral, S.A.", [new("Acería de Avilés", "Avilés"), new("Parque de chatarra de Avilés", "Avilés")]),
        new("Farmacéutica Olmedia, S.L.", [new("Planta de producción de Alcalá de Henares", "Alcalá de Henares")])
    ]);

    public static TenantPilotoOutbound T5 { get; } = new("T5", NombreTenantT5, EscenarioPilotoOutbound.Esqueleto, false, 0, []);

    public static TenantPilotoOutbound T6 { get; } = new("T6", NombreTenantT6, EscenarioPilotoOutbound.Esqueleto, true, 0, []);

    /// <summary>T1..T6, en el orden de la matriz.</summary>
    public static IReadOnlyList<TenantPilotoOutbound> Tenants { get; } = [T1, T2, T3, T4, T5, T6];

    /// <summary>
    /// El orden en que se siembran: los pequeños primero y T1 el último, para que una
    /// ejecución en un servidor con poca memoria haya dejado hecho lo más posible si
    /// se interrumpe en el grande.
    /// </summary>
    public static IReadOnlyList<TenantPilotoOutbound> EnOrdenDeSiembra { get; } = [T4, T3, T2, T5, T6, T1];

    /// <summary>Los siete Tenants, con el del Operador CAE externo al final: es el orden de retirada.</summary>
    public static IReadOnlyList<string> NombresTenants { get; } = [.. Tenants.Select(t => t.Nombre), NombreTenantOperador];

    public static readonly string[] NombresDePila =
        ["Lucía", "Andrés", "Noelia", "Rubén", "Paula", "Héctor", "Irene", "Óscar", "Carla", "Mateo", "Sonia", "Adrián", "Elena", "Javier", "Nuria", "Sergio"];

    public static readonly string[] Apellidos =
    [
        "Navarro Gil", "Ferrer Pons", "Molina Ríos", "Soler Vidal", "Cano Prieto", "Ibarra Sanz", "Lozano Marín", "Vega Ortiz",
        "Campos Lara", "Pastor Rey", "Salas Duque", "Bravo Nieto", "Herrero Luna", "Gallego Mora", "Santos Peña", "Crespo Vidal"
    ];

    /// <summary>
    /// Los valores de la matriz que la autoverificación exige de cada Tenant
    /// propietario; <c>null</c> para un esqueleto (todavía sin datos que medir).
    /// Escritos a mano, no derivados del sembrador.
    /// </summary>
    public static EsperadoPilotoOutbound? Esperado(TenantPilotoOutbound tenant, VarianteT4PilotoOutbound varianteT4) =>
        tenant.Escenario switch
        {
            EscenarioPilotoOutbound.TodoAlDia => new(
                FilasMiTrabajo: 0, CumplimientoInicio: 100, CumplimientoVisionCartera: 100, CumplimientoEmpresa: 100,
                Centros: [.. tenant.Centros.Select(c => new EsperadoCentroPilotoOutbound(c.Nombre, EstadoCentro.Vigente, 100))],
                ParesExigidos: 65, ParesFaltantes: 0, Documentos: 77, TodoAlDia: true),

            // Cada Trabajador: un Vencido (bloqueo por vigencia) y un Faltante en el segundo Centro de su pareja.
            EscenarioPilotoOutbound.Mitad => new(
                FilasMiTrabajo: 20, CumplimientoInicio: 50, CumplimientoVisionCartera: 50, CumplimientoEmpresa: 50,
                Centros:
                [
                    new(tenant.Centros[0].Nombre, EstadoCentro.Vencido, 50), new(tenant.Centros[1].Nombre, EstadoCentro.Faltante, 50),
                    new(tenant.Centros[2].Nombre, EstadoCentro.Vencido, 50), new(tenant.Centros[3].Nombre, EstadoCentro.Faltante, 50)
                ],
                ParesExigidos: 40, ParesFaltantes: 10, Documentos: 20, TodoAlDia: false),

            EscenarioPilotoOutbound.TodoPendiente when varianteT4 == VarianteT4PilotoOutbound.SinDocumentos => new(
                FilasMiTrabajo: 19, CumplimientoInicio: null, CumplimientoVisionCartera: null, CumplimientoEmpresa: 0,
                Centros: [.. tenant.Centros.Select(c => new EsperadoCentroPilotoOutbound(c.Nombre, EstadoCentro.Faltante, 0))],
                ParesExigidos: 19, ParesFaltantes: 19, Documentos: 0, TodoAlDia: false,
                Divergencia:
                    "sin ningún documento, Inicio y Visión de cartera no pintan 0 %: cuentan documentos existentes, no pares " +
                    "exigidos. Inicio pinta el anillo «100%» con «0 de 0 documentos al día en cartera» y Visión de cartera pinta " +
                    "«100%» en su fila; Centros y Empresas sí dan 0 %. Para ver 0 % en las cuatro pantallas, sembrar con " +
                    $"{OpcionesPilotoOutbound.ClaveVarianteT4}={nameof(VarianteT4PilotoOutbound.DocumentosVencidos)}."),

            EscenarioPilotoOutbound.TodoPendiente => new(
                FilasMiTrabajo: 19, CumplimientoInicio: 0, CumplimientoVisionCartera: 0, CumplimientoEmpresa: 0,
                Centros: [.. tenant.Centros.Select(c => new EsperadoCentroPilotoOutbound(c.Nombre, EstadoCentro.Vencido, 0))],
                ParesExigidos: 19, ParesFaltantes: 0, Documentos: 19, TodoAlDia: false),

            _ => null
        };
}
