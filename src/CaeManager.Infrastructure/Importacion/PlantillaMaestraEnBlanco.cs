using ClosedXML.Excel;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Libro en blanco de la plantilla maestra del Importador (Paso 1), para la descarga de /importacion. Escribe la hoja
/// «Instrucciones» y una hoja por entidad con la cabecera en la fila 1 (rótulo exacto, «*» en las obligatorias y la
/// columna «Control (no rellenar)», que el lector ignora) y un ejemplo ficticio en la fila 2 con el marcador
/// <c>EJEMPLO</c>, que el lector salta. Los rótulos y los nombres de hoja son el contrato de
/// <see cref="ClosedXmlPlantillaMaestraService"/>: las columnas se localizan por nombre, no por posición.
/// Sin credenciales ni columnas de salud: la plantilla no las tiene (decisiones D del Importador).
/// </summary>
internal static class PlantillaMaestraEnBlanco
{
    private const string Marcador = "EJEMPLO – ";

    private sealed record Hoja(string Nombre, string[] Columnas, Dictionary<string, string> Ejemplo);

    private static readonly Hoja[] Hojas =
    [
        new("Empresas",
            ["Razón social *", "CIF *", "Carpeta actual", "CNAE", "Convenio aplicable", "Actividad Anexo I (S/N)",
             "Modalidad de organización preventiva", "Organización SPA", "Contacto SPA nombre", "Contacto SPA teléfono",
             "Contacto SPA correo", "Mutua", "Técnico PRL nombre", "Técnico PRL teléfono", "Técnico PRL correo",
             "Contacto formación nombre", "Contacto formación teléfono", "Contacto formación correo",
             "Canal de firma de documentos", "Firmante de documentos", "Sello custodiado por el Operador CAE (S/N)",
             "Notas", "Control (no rellenar)"],
            new()
            {
                ["Razón social *"] = Marcador + "Industrias Ejemplo, S.L.", ["CIF *"] = "B12345674", ["Carpeta actual"] = "Industrias Ejemplo",
                ["CNAE"] = "4321", ["Convenio aplicable"] = "Construcción y obras públicas", ["Actividad Anexo I (S/N)"] = "N"
            }),
        new("Subcontratas",
            ["Razón social de la Subcontrata *", "CIF de la Subcontrata *", "CIF de la Empresa contratante *",
             "CIF de la subcontratante de segundo nivel", "Nivel de servicio *", "Vigente desde", "Carpeta actual",
             "Control (no rellenar)"],
            new()
            {
                ["Razón social de la Subcontrata *"] = Marcador + "Montajes Ejemplo, S.L.", ["CIF de la Subcontrata *"] = "A58818501",
                ["CIF de la Empresa contratante *"] = "B12345674", ["Nivel de servicio *"] = "Gestionada", ["Vigente desde"] = "2024-03-01"
            }),
        new("Clientes empresariales",
            ["Razón social *", "CIF *", "Crítico (C/N) *", "CIF de la Empresa gestionada *", "Nota interna", "Control (no rellenar)"],
            new()
            {
                ["Razón social *"] = Marcador + "Distribuciones Ejemplo, S.A.", ["CIF *"] = "A58818501", ["Crítico (C/N) *"] = "N",
                ["CIF de la Empresa gestionada *"] = "B12345674"
            }),
        new("Centros",
            ["Nombre del Centro *", "CIF del Cliente empresarial *", "CIF de la Empresa gestionada *", "Código del Centro",
             "Dirección *", "Contacto", "Contrato vigente hasta", "Modalidad de gestión CAE *", "Horario de acceso",
             "Control de accesos nombre", "Control de accesos teléfono", "Técnico de prevención nombre",
             "Técnico de prevención teléfono", "Técnico de prevención correo", "Trabajos en altura (S/N)",
             "Espacios confinados (S/N)", "Atmósferas explosivas (S/N)", "Notas", "Control (no rellenar)"],
            new()
            {
                ["Nombre del Centro *"] = Marcador + "Planta Norte", ["CIF del Cliente empresarial *"] = "A58818501",
                ["CIF de la Empresa gestionada *"] = "B12345674", ["Código del Centro"] = "PN-01",
                ["Dirección *"] = "Calle Ficticia 1, 28000 Ciudad", ["Contrato vigente hasta"] = "2027-12-31",
                ["Modalidad de gestión CAE *"] = "Con gestión CAE", ["Horario de acceso"] = "L-V 7:00-19:00"
            }),
        new("Accesos por Centro",
            ["CIF del Cliente empresarial *", "Nombre del Centro *", "Tipo de canal *", "Etiqueta del canal *",
             "Plataforma (catálogo)", "URL de acceso", "Es el canal principal (S/N)", "Correos destinatarios",
             "Nombre de contacto", "Notas", "Control (no rellenar)"],
            new()
            {
                ["CIF del Cliente empresarial *"] = Marcador + "A58818501", ["Nombre del Centro *"] = "Planta Norte",
                ["Tipo de canal *"] = "Plataforma", ["Etiqueta del canal *"] = "Documentación de Trabajadores"
            }),
        new("Trabajadores",
            ["Nombre *", "Apellidos *", "Tipo de identidad *", "Número de identidad *", "CIF de la Empresa *",
             "Carpeta actual *", "Categoría / Puesto *", "Fecha de nacimiento *", "Nacionalidad *", "Tipo de contratación *",
             "Fecha de alta en la Empresa *", "Fecha de baja en la Empresa", "Número de afiliación a la Seguridad Social",
             "Tipo de vínculo", "Figura en RNT", "Tipo de permiso de trabajo", "Caducidad del permiso de trabajo",
             "Motivo del permiso", "Correo", "Teléfono", "Contacto de emergencia nombre", "Contacto de emergencia teléfono",
             "Observaciones", "Control (no rellenar)"],
            new()
            {
                ["Nombre *"] = Marcador + "Nombre Ficticio", ["Apellidos *"] = "Apellido Ejemplo", ["Tipo de identidad *"] = "DNI",
                ["Número de identidad *"] = "12345678Z", ["CIF de la Empresa *"] = "B12345674",
                ["Carpeta actual *"] = "Apellido Ejemplo Nombre", ["Categoría / Puesto *"] = "Oficial 1ª",
                ["Fecha de nacimiento *"] = "1985-06-15", ["Nacionalidad *"] = "Española", ["Tipo de contratación *"] = "Cuenta ajena",
                ["Fecha de alta en la Empresa *"] = "2022-01-10", ["Correo"] = "trabajador@ejemplo.invalid", ["Teléfono"] = "+34 600 000 010"
            }),
        new("Asignaciones",
            ["Número de identidad del Trabajador *", "CIF del Cliente empresarial *", "Nombre del Centro *",
             "Fecha de alta en el Centro", "Fecha de baja en el Centro", "Plataforma de alta", "Fecha de alta en la plataforma",
             "Control (no rellenar)"],
            new()
            {
                ["Número de identidad del Trabajador *"] = Marcador + "12345678Z", ["CIF del Cliente empresarial *"] = "A58818501",
                ["Nombre del Centro *"] = "Planta Norte", ["Fecha de alta en el Centro"] = "2024-03-01"
            }),
        new("Vehículos",
            ["CIF de la Empresa o Subcontrata *", "Nombre *", "Modelo *", "Matrícula *", "Tipo", "Titularidad",
             "Revisión de grúa", "Declaración CE", "Aseguradora", "Póliza", "Recibo", "Carpeta actual", "Notas",
             "Control (no rellenar)"],
            new()
            {
                ["CIF de la Empresa o Subcontrata *"] = Marcador + "B12345674", ["Nombre *"] = "Furgoneta 1",
                ["Modelo *"] = "Modelo Ejemplo", ["Matrícula *"] = "0000 BBB", ["Tipo"] = "Furgoneta", ["Titularidad"] = "Propiedad"
            }),
        new("Tipos de documento",
            ["Carpeta actual (nombre tal como está) *", "Ámbito *", "Tratamiento *", "Tipo de documento TALVEG", "Notas"],
            new()
            {
                ["Carpeta actual (nombre tal como está) *"] = Marcador + "Certificado Seguridad Social", ["Ámbito *"] = "Empresa",
                ["Tratamiento *"] = "Tipo de Documento",
                ["Tipo de documento TALVEG"] = "Certificado de estar al corriente con la Seguridad Social"
            })
    ];

    /// <summary>Nombres de las hojas de datos, en el orden del libro (tras «Instrucciones»).</summary>
    internal static IReadOnlyList<string> NombresDeHojas => [.. Hojas.Select(h => h.Nombre)];

    internal static byte[] Generar()
    {
        using var libro = new XLWorkbook();

        var instrucciones = libro.Worksheets.Add("Instrucciones");
        instrucciones.Cell(1, 1).Value = "PLANTILLA MAESTRA DEL IMPORTADOR DE TALVEG · Paso 1";
        instrucciones.Cell(1, 1).Style.Font.Bold = true;
        string[] lineas =
        [
            "Una sola plantilla para todas las entidades. Todos los datos de ejemplo son ficticios.",
            "La cabecera de cada hoja está en la fila 1 y no se renombra: las columnas se localizan por su nombre. Las marcadas con * son obligatorias.",
            "La fila 2 de cada hoja es un ejemplo que empieza por «EJEMPLO»: se salta al importar; bórrala o déjala.",
            "Los datos empiezan en la fila 2 (o en la 3 si conservas el ejemplo). «Control (no rellenar)» se ignora.",
            "Un archivo corresponde a una sola organización. Repetir el archivo no duplica nada: se deduplica por CIF, identidad y Cliente empresarial + Centro.",
            "Sin credenciales: la plantilla no lleva usuarios ni contraseñas de plataformas.",
            "Nada médico: no escribas diagnósticos, bajas ni restricciones en Observaciones o Notas."
        ];
        for (var i = 0; i < lineas.Length; i++)
            instrucciones.Cell(i + 3, 1).Value = lineas[i];
        instrucciones.Column(1).AdjustToContents();

        foreach (var definicion in Hojas)
        {
            var hoja = libro.Worksheets.Add(definicion.Nombre);
            for (var c = 0; c < definicion.Columnas.Length; c++)
            {
                hoja.Cell(1, c + 1).Value = definicion.Columnas[c];
                if (definicion.Ejemplo.TryGetValue(definicion.Columnas[c], out var ejemplo))
                    hoja.Cell(2, c + 1).Value = ejemplo;
            }

            hoja.Row(1).Style.Font.Bold = true;
            hoja.Columns().AdjustToContents();
        }

        using var stream = new MemoryStream();
        libro.SaveAs(stream);
        return stream.ToArray();
    }
}
