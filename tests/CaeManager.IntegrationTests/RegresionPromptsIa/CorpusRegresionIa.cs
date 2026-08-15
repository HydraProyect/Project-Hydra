namespace CaeManager.IntegrationTests.RegresionPromptsIa;

/// <summary>
/// Un documento del corpus fijo de regresión de prompts (MACRO_PLAN § 6.6):
/// texto sintético representativo de un tipo de documento CAE/PRL real (ver
/// TipoDocumentoSeedData) con la extracción que se espera de cualquier
/// proveedor de IA documental que implemente el prompt de extracción
/// estructurada (ver AnthropicDocumentAIProvider.SystemPromptEstructurado,
/// que fija el contrato de campos que aquí se comprueba).
///
/// Nunca contiene datos reales — nombres, DNI, CIF y matrícula son
/// inventados a propósito (repo público, ver CLAUDE.md).
///
/// <paramref name="CamposExactos"/>: campos estructurados donde se exige
/// coincidencia exacta (fechas ISO, DNI, CIF) — no hay ambigüedad de
/// redacción posible, y una discrepancia es una regresión real.
/// <paramref name="CamposQueDebenContener"/>: campos de texto libre
/// (nombres de persona/empresa) donde solo se exige que el valor extraído
/// contenga el fragmento esperado — un LLM real puede añadir tratamiento
/// ("D.", "Sr.") o reordenar sin que sea una regresión.
/// </summary>
public sealed record DocumentoCorpusIa(
    string Nombre,
    string TipoEsperado,
    string Texto,
    int ConfianzaMinimaEsperada,
    IReadOnlyDictionary<string, string> CamposExactos,
    IReadOnlyDictionary<string, string> CamposQueDebenContener);

/// <summary>
/// Corpus deliberadamente pequeño (5 documentos) y fijo: crecerlo es barato
/// cuando haga falta, pero el valor de la suite es que corra rápido en cada
/// cambio de prompt/modelo, no cubrir cada tipo de documento del catálogo.
/// Cada entrada ejercita una regla distinta del prompt maestro (ver
/// AnthropicDocumentAIProvider.SystemPromptEstructurado): vencimiento
/// calculado desde un periodo de vigencia, vencimiento explícito, artículo
/// de ley, especificidad de puesto y detección de firma.
/// </summary>
public static class CorpusRegresionIa
{
    public static IReadOnlyList<DocumentoCorpusIa> Documentos { get; } =
    [
        new DocumentoCorpusIa(
            Nombre: "Apto médico laboral",
            TipoEsperado: "Apto médico laboral",
            Texto: """
                SERVICIO DE PREVENCIÓN MANCOMUNADO FICTICIO
                CERTIFICADO DE APTITUD MÉDICA

                Trabajador: Marta Ibáñez Rodríguez
                DNI: 11223344B
                Puesto evaluado: Operario de mantenimiento

                Tras la vigilancia de la salud realizada conforme al protocolo
                aplicable, se declara al trabajador arriba indicado:

                APTO PARA EL PUESTO DE TRABAJO

                Fecha de reconocimiento: 03/02/2026
                Válido por 12 meses desde la fecha de reconocimiento.

                Firmado electrónicamente por el Servicio de Prevención.
                """,
            ConfianzaMinimaEsperada: 60,
            CamposExactos: new Dictionary<string, string>
            {
                ["documentoIdentidadTrabajador"] = "11223344B",
                ["fechaEmision"] = "2026-02-03",
                ["fechaVencimiento"] = "2027-02-03",
                ["tieneFirma"] = "true"
            },
            CamposQueDebenContener: new Dictionary<string, string>
            {
                ["nombreTrabajador"] = "Ibáñez"
            }),

        new DocumentoCorpusIa(
            Nombre: "Formación Art. 19 PRL",
            TipoEsperado: "Formación Art. 19",
            Texto: """
                ASOCIACIÓN FORMATIVA PRL FICTICIA
                DIPLOMA DE FORMACIÓN EN PREVENCIÓN DE RIESGOS LABORALES

                Se certifica que D. Ricardo Fernández Soto ha superado la
                formación teórico-práctica en materia de prevención de
                riesgos laborales conforme al artículo 19 de la Ley 31/1995
                de Prevención de Riesgos Laborales, específica para el
                puesto de electricista de mantenimiento industrial.

                Duración: 20 horas
                Fecha de expedición: 15/01/2026

                Firma del formador: [firmado]
                """,
            ConfianzaMinimaEsperada: 60,
            CamposExactos: new Dictionary<string, string>
            {
                ["fechaEmision"] = "2026-01-15",
                ["tieneFirma"] = "true",
                ["especificoPuestoTrabajo"] = "true"
            },
            CamposQueDebenContener: new Dictionary<string, string>
            {
                ["nombreTrabajador"] = "Fernández",
                ["articuloLey"] = "19"
            }),

        new DocumentoCorpusIa(
            Nombre: "RNT/TC2 de empresa",
            TipoEsperado: "RNT/TC2",
            Texto: """
                TESORERÍA GENERAL DE LA SEGURIDAD SOCIAL (SIMULACIÓN)
                RELACIÓN NOMINAL DE TRABAJADORES (RNT) — MODELO TC2

                Empresa: Construcciones Ejemplo S.L.
                CIF: B12345678
                Código de cuenta de cotización: 28/1234567/89
                Periodo de liquidación: Enero 2026

                Documento generado electrónicamente por el Sistema RED.
                Fecha de generación: 05/02/2026
                """,
            ConfianzaMinimaEsperada: 60,
            CamposExactos: new Dictionary<string, string>
            {
                ["cifEmpresa"] = "B12345678",
                ["fechaEmision"] = "2026-02-05"
            },
            CamposQueDebenContener: new Dictionary<string, string>
            {
                ["nombreEmpresa"] = "Construcciones Ejemplo"
            }),

        new DocumentoCorpusIa(
            Nombre: "Certificado de estar al corriente TGSS",
            TipoEsperado: "Certificado de estar al corriente con la Seguridad Social",
            Texto: """
                TESORERÍA GENERAL DE LA SEGURIDAD SOCIAL (SIMULACIÓN)
                CERTIFICADO DE ESTAR AL CORRIENTE EN EL PAGO DE
                OBLIGACIONES CON LA SEGURIDAD SOCIAL

                Se certifica que la empresa Instalaciones Ejemplo S.A., con
                CIF A87654321, se encuentra al corriente de sus obligaciones
                con la Seguridad Social a fecha de expedición de este
                certificado.

                Fecha de expedición: 10/02/2026
                Validez: 1 mes desde la fecha de expedición.
                """,
            ConfianzaMinimaEsperada: 60,
            CamposExactos: new Dictionary<string, string>
            {
                ["cifEmpresa"] = "A87654321",
                ["fechaEmision"] = "2026-02-10",
                ["fechaVencimiento"] = "2026-03-10"
            },
            CamposQueDebenContener: new Dictionary<string, string>
            {
                ["nombreEmpresa"] = "Instalaciones Ejemplo"
            }),

        new DocumentoCorpusIa(
            Nombre: "Seguro de vehículo",
            TipoEsperado: "Seguro (Vehículo)",
            Texto: """
                COMPAÑÍA DE SEGUROS FICTICIA S.A.
                CERTIFICADO DE SEGURO DE RESPONSABILIDAD CIVIL DE VEHÍCULOS

                Matrícula: 1234ABC
                Tomador: Transportes Ejemplo S.L.
                Vigencia: desde el 01/03/2026 hasta el 28/02/2027

                Este certificado acredita la existencia de cobertura de
                seguro obligatorio para el vehículo indicado.

                Firmado digitalmente por la compañía aseguradora.
                """,
            ConfianzaMinimaEsperada: 60,
            CamposExactos: new Dictionary<string, string>
            {
                ["fechaEmision"] = "2026-03-01",
                ["fechaVencimiento"] = "2027-02-28",
                ["tieneFirma"] = "true"
            },
            CamposQueDebenContener: new Dictionary<string, string>
            {
                ["nombreEmpresa"] = "Transportes Ejemplo"
            })
    ];

    public static IEnumerable<object[]> ComoTheoryData() =>
        Documentos.Select(documento => new object[] { documento });
}
