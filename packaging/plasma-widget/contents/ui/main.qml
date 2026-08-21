import QtQuick
import QtQuick.Layouts
import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents3
import org.kde.plasma.core as PlasmaCore
import org.kde.plasma.plasmoid

PlasmoidItem {
    id: root

    property var agendaEvents: []
    property string connectionState: "loading"
    property date lastUpdated: new Date(0)
    property var availableUpdate: null
    readonly property var nextEvent: agendaEvents.length > 0 ? agendaEvents[0] : null

    preferredRepresentation: Plasmoid.formFactor === PlasmaCore.Types.Planar ? fullRepresentation : null

    switchWidth: Plasmoid.formFactor === PlasmaCore.Types.Planar ? -1 : Kirigami.Units.gridUnit * 18
    switchHeight: Plasmoid.formFactor === PlasmaCore.Types.Planar ? -1 : Kirigami.Units.gridUnit * 12
    toolTipMainText: nextEvent ? nextEvent.title : i18n("iCloud Agenda")
    toolTipSubText: nextEvent
        ? formatEventTime(nextEvent) + (nextEvent.location ? " · " + nextEvent.location : "")
        : connectionState === "ready" ? i18n("Your schedule is clear") : i18n("Local calendar is unavailable")

    function formatEventTime(event) {
        if (event.isAllDay) return i18n("All day")
        const value = new Date(event.startsAt)
        const end = new Date(event.endsAt)
        const today = new Date()
        const sameDay = value.toDateString() === today.toDateString()
        const range = Qt.formatTime(value, Qt.locale().timeFormat(Locale.ShortFormat)) + " - "
            + Qt.formatTime(end, Qt.locale().timeFormat(Locale.ShortFormat))
        return sameDay ? range : Qt.formatDate(value, "ddd") + " · " + range
    }

    function calendarColor(value) {
        // Apple uses CSS-style #RRGGBBAA. Qt parses eight-digit hex as
        // #AARRGGBB, so remove Apple's opaque alpha channel for an exact match.
        if (typeof value === "string" && /^#[0-9a-fA-F]{8}$/.test(value))
            return value.substring(0, 7)
        return value || Kirigami.Theme.highlightColor
    }

    function refresh() {
        const separator = plasmoid.configuration.endpoint.indexOf("?") >= 0 ? "&" : "?"
        const dayStart = new Date()
        dayStart.setHours(0, 0, 0, 0)
        const dayEnd = new Date(dayStart)
        dayEnd.setDate(dayEnd.getDate() + 1)
        const query = "from=" + encodeURIComponent(dayStart.toISOString())
            + "&to=" + encodeURIComponent(dayEnd.toISOString())
            + "&day=" + Qt.formatDate(dayStart, "yyyy-MM-dd")
            + "&limit=" + plasmoid.configuration.maximumEvents
        const request = new XMLHttpRequest()
        request.open("GET", plasmoid.configuration.endpoint + separator + query)
        request.onreadystatechange = function() {
            if (request.readyState !== XMLHttpRequest.DONE) return
            if (request.status >= 200 && request.status < 300) {
                try {
                    const payload = JSON.parse(request.responseText)
                    root.agendaEvents = payload.events || []
                    root.connectionState = "ready"
                    root.lastUpdated = new Date()
                    return
                } catch (error) {
                    console.warn("iCloud Agenda received invalid local data")
                }
            }
            root.connectionState = "offline"
        }
        request.send()
    }

    function checkForUpdate() {
        const request = new XMLHttpRequest()
        request.open("GET", "http://127.0.0.1:5088/api/update")
        request.onreadystatechange = function() {
            if (request.readyState !== XMLHttpRequest.DONE || request.status < 200 || request.status >= 300) return
            try {
                const payload = JSON.parse(request.responseText)
                root.availableUpdate = payload.updateAvailable ? payload : null
            } catch (error) {
                console.warn("iCloud Agenda received invalid update data")
            }
        }
        request.send()
    }

    compactRepresentation: MouseArea {
        id: compact
        implicitWidth: compactRow.implicitWidth + Kirigami.Units.largeSpacing * 2
        implicitHeight: Kirigami.Units.gridUnit * 2
        hoverEnabled: true
        onClicked: root.expanded = !root.expanded
        Accessible.name: root.toolTipMainText + ", " + root.toolTipSubText

        RowLayout {
            id: compactRow
            anchors.centerIn: parent
            spacing: Kirigami.Units.smallSpacing
            Kirigami.Icon {
                source: root.connectionState === "offline" ? "network-disconnect" : "view-calendar-upcoming-events"
                Layout.preferredWidth: Kirigami.Units.iconSizes.smallMedium
                Layout.preferredHeight: width
            }
            ColumnLayout {
                spacing: 0
                PlasmaComponents3.Label {
                    text: root.nextEvent ? root.formatEventTime(root.nextEvent) : i18n("Clear")
                    font.bold: true
                }
                PlasmaComponents3.Label {
                    visible: root.nextEvent !== null
                    text: root.nextEvent ? root.nextEvent.title : ""
                    opacity: 0.72
                    elide: Text.ElideRight
                    Layout.maximumWidth: Kirigami.Units.gridUnit * 10
                }
            }
        }
    }

    fullRepresentation: Item {
        implicitWidth: Kirigami.Units.gridUnit * 20
        implicitHeight: Kirigami.Units.gridUnit * 25

        ColumnLayout {
            anchors.fill: parent
            anchors.margins: Kirigami.Units.largeSpacing
            spacing: Kirigami.Units.largeSpacing

            RowLayout {
                Layout.fillWidth: true
                ColumnLayout {
                    spacing: 0
                    Kirigami.Heading { text: i18n("Today"); level: 2 }
                    PlasmaComponents3.Label {
                        text: root.connectionState === "offline"
                            ? i18n("Showing the last local snapshot")
                            : i18np("%1 event today", "%1 events today", root.agendaEvents.length)
                        opacity: 0.65
                    }
                }
                Item { Layout.fillWidth: true }
                PlasmaComponents3.ToolButton {
                    visible: root.availableUpdate !== null
                    icon.name: "software-update-available"
                    text: root.availableUpdate ? i18n("Update to version %1", root.availableUpdate.latestVersion) : ""
                    onClicked: if (root.availableUpdate) Qt.openUrlExternally(root.availableUpdate.releaseUrl)
                }
                PlasmaComponents3.ToolButton {
                    icon.name: "list-add"
                    text: i18n("Add event")
                    onClicked: Qt.openUrlExternally("icloud-calendar://add-event")
                }
                PlasmaComponents3.ToolButton {
                    icon.name: "view-refresh"
                    text: i18n("Refresh")
                    onClicked: root.refresh()
                }
            }

            ListView {
                id: eventList
                Layout.fillWidth: true
                Layout.fillHeight: true
                clip: true
                spacing: Kirigami.Units.smallSpacing
                model: root.agendaEvents
                delegate: Rectangle {
                    required property var modelData
                    width: eventList.width
                    height: eventContent.implicitHeight + Kirigami.Units.largeSpacing * 2
                    radius: Kirigami.Units.cornerRadius
                    color: Kirigami.Theme.alternateBackgroundColor
                    RowLayout {
                        id: eventContent
                        anchors.fill: parent
                        anchors.margins: Kirigami.Units.largeSpacing
                        spacing: Kirigami.Units.largeSpacing
                        Rectangle {
                            Layout.preferredWidth: 4
                            Layout.fillHeight: true
                            radius: 2
                            color: root.calendarColor(modelData.color)
                        }
                        ColumnLayout {
                            Layout.fillWidth: true
                            spacing: Kirigami.Units.smallSpacing
                            PlasmaComponents3.Label {
                                text: root.formatEventTime(modelData)
                                color: Kirigami.Theme.highlightColor
                                font.bold: true
                            }
                            PlasmaComponents3.Label {
                                text: modelData.title
                                font.bold: true
                                elide: Text.ElideRight
                                Layout.fillWidth: true
                            }
                            PlasmaComponents3.Label {
                                visible: Boolean(modelData.location)
                                text: modelData.location || ""
                                opacity: 0.68
                                elide: Text.ElideRight
                                Layout.fillWidth: true
                            }
                        }
                    }
                }

                PlasmaComponents3.Label {
                    anchors.centerIn: parent
                    visible: eventList.count === 0
                    text: root.connectionState === "offline"
                        ? i18n("Start Linux iCloud Calendar to reconnect")
                        : i18n("Nothing scheduled. Enjoy the space.")
                    opacity: 0.68
                    horizontalAlignment: Text.AlignHCenter
                }
            }
        }
    }

    Timer {
        interval: Math.max(1000, plasmoid.configuration.refreshInterval * 1000)
        repeat: true
        running: true
        triggeredOnStart: true
        onTriggered: root.refresh()
    }

    Timer {
        interval: 6 * 60 * 60 * 1000
        repeat: true
        running: true
        triggeredOnStart: true
        onTriggered: root.checkForUpdate()
    }
}
