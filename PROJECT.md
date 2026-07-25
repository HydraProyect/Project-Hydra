# CAE Manager — Visión de Producto

## Qué es

CAE Manager es el software que reemplaza por completo el actual "Cuadro de Control CAE" en Excel: la herramienta con la que un departamento de Prevención de Riesgos Laborales (PRL) coordina la documentación de sus trabajadores frente a los clientes en cuyos centros de trabajo prestan servicio (Coordinación de Actividades Empresariales, CAE).

El Excel de referencia (`CAE_KHS_Cuadro_de_Control_2.xlsx`) es el documento fuente de este proyecto: define, con datos reales, qué información se gestiona hoy, qué reglas de vigencia aplican y qué fricciones tiene el proceso actual. Todo el modelo de dominio de CAE Manager está validado contra ese archivo (ver `DATABASE.md`).

## A quién sirve

Hydra es un **producto SaaS multi-tenant** (decisión 2026-07-23, `ADR-003-saas-multitenant.md`) para dos perfiles de comprador: **consultoras de PRL** que gestionan la CAE de varias empresas contratistas a la vez, y **empresas contratistas** que gestionan la suya propia. Cada organización compradora es un *tenant* aislado — la frontera absoluta del sistema (ver `docs/MULTITENANCY.md`, con los dos escenarios de negocio desarrollados).

Estado del despliegue: la instalación actual sirve a una única organización (~10 usuarios simultáneos, perfil PRL / gestión CAE), que pasará a ser el tenant #1 cuando se complete la implementación del aislamiento multi-tenant (en curso, ver `ROADMAP.md`). Arquitectura preparada para crecer en tenants, usuarios, clientes, centros y volumen documental sin rediseño.

## El problema que resolvemos

Hoy la información vive en una hoja de cálculo con:
- Datos de cliente/centro mezclados en texto libre (un cliente con varios centros aparece como una sola fila con los centros listados dentro de una celda).
- Cálculo manual de vigencias y semáforos mediante fórmulas frágiles.
- Credenciales de acceso a portales externos de clientes en texto plano, en la misma hoja que comparten varias personas.
- Sin historial de cambios, sin control de quién modificó qué, sin roles.
- Búsqueda de un trabajador o centro mediante un sistema de filtros manual y propenso a error.

CAE Manager reemplaza esto con un sistema normalizado, auditable, con control de acceso por rol y una experiencia de uso que permite encontrar cualquier dato en menos de tres clics.

## Filosofía del producto

Cada decisión de diseño reduce la carga mental del usuario. La medida de éxito no es "¿tiene el CRUD todos los campos del Excel?" sino "¿el usuario encuentra lo que busca sin pensar dónde buscarlo?".

El sistema es minimalista, rápido, intuitivo, profesional, elegante, consistente y confiable. No es una aplicación llena de botones: es una herramienta que desaparece mientras el usuario trabaja.

Personalidad: profesional, precisa, moderna, tecnológica, ordenada, amigable. Nunca infantil, nunca excesivamente corporativa, nunca recargada. Referencias: Linear, Stripe Dashboard, Vercel, Notion, Raycast, GitHub, Microsoft Fluent.

## Principios de decisión

Cuando dos enfoques compiten, se resuelve en este orden:

1. **UX nunca se sacrifica por velocidad de desarrollo.**
2. **Simplicidad y mantenibilidad** por encima de flexibilidad especulativa (YAGNI).
3. **Consistencia de patrones** por encima de la solución "óptima" puntual — un patrón repetido en 16 módulos vale más que 16 soluciones ligeramente mejores pero distintas entre sí.
4. Todo mensaje al usuario se escribe pensando en la persona que hoy usa el Excel, no en el desarrollador.

## Glosario de dominio (validado contra el Excel real)

| Término | Significado |
|---|---|
| **Cliente** | Empresa titular de uno o más Centros de Trabajo (p. ej. COBEGA, Damm, Heineken, Mahou). No opera el sistema; es el destinatario de la coordinación CAE. |
| **Centro** (o "centro/plataforma") | Ubicación física de un Cliente donde trabajan nuestros trabajadores. Un Cliente puede tener varios Centros. En el Excel actual esto está mal normalizado (varios centros listados como texto dentro de una fila de cliente); en CAE Manager cada Centro es su propia entidad. |
| **Plataforma de acceso** | Portal externo (p. ej. CTAIMA CAE) que un Cliente exige usar para acreditar documentación. Tiene URL y credenciales — dato sensible, se cifra en reposo. |
| **Empresa** | La empresa contratista cuyo personal se coordina (la organización que usa CAE Manager). Puede tener más de una razón social (p. ej. una entidad para personal nacional y otra para personal extranjero). |
| **Trabajador** | Empleado de una Empresa. Tiene datos personales y una colección de Documentos. |
| **Tipo de Documento** | Catálogo maestro de documentos PRL exigibles (apto médico, EPIS, formación, reciclajes, etc.), cada uno con su vigencia en meses (si aplica) y si genera vencimiento automático. |
| **Documento** | Instancia de un Tipo de Documento asociada a un Trabajador: fecha de emisión, vencimiento calculado, estado (vigente/próximo/urgente/vencido/no aplica) y archivo adjunto. |
| **Asignación** | Relación entre un Trabajador y un Centro (con fecha de alta/baja) — determina dónde está activo cada trabajador. |
| **Requisito documental** | Exigencia adicional específica de un Centro, más allá de la documentación base común a todos los centros. Puede bloquear el acceso del trabajador si no se cumple. |
| **Alerta** | Notificación generada cuando un Documento entra en umbral de aviso o vence. |
| **Usuario / Rol** | Cuentas internas del sistema con roles: Administrador, Supervisor, Ejecutivo CAE, Consulta. |

## Objetivo de usabilidad

El usuario debe poder encontrar cualquier información en menos de tres clics. Esto es un criterio de aceptación, no una aspiración: cada pantalla nueva se valida contra esta regla antes de darse por terminada.

## Documentos relacionados

- `ARCHITECTURE.md` — arquitectura técnica, capas, patrones.
- `DOMAIN.md` — modelo de dominio: agregados, relaciones, invariantes.
- `DATABASE.md` — modelo de datos, reglas de negocio, mapeo desde el Excel real.
- `docs/MULTITENANCY.md` — normativa multi-tenant (aislamiento, catálogos, resolución de tenant).
- `DESIGN_SYSTEM.md` — identidad visual, tokens, catálogo de componentes.
- `UX_PATTERNS.md` — patrones de interacción y microcopy.
- `CODING_STANDARDS.md` — convenciones de código.
- `ROADMAP.md` — fases de entrega y criterios de aceptación.
