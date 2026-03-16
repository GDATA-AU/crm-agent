<?xml version="1.0" encoding="utf-8"?>
<!--
  Heat post-processing transform.
  Removes components for files that are declared explicitly in Product.wxs
  (they carry ServiceInstall / ServiceControl and must not be duplicated).
-->
<xsl:stylesheet version="1.0"
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:wix="http://wixtoolset.org/schemas/v4/wxs">

  <xsl:output method="xml" indent="yes" />

  <!-- Identity: copy everything by default -->
  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <!-- Suppress CrmAgent.exe (handled explicitly with ServiceInstall) -->
  <xsl:template match="wix:Component[wix:File[contains(@Source, '\CrmAgent.exe')]]" />

  <!-- Suppress appsettings.json -->
  <xsl:template match="wix:Component[wix:File[contains(@Source, '\appsettings.json')]]" />

  <!-- Suppress appsettings.Development.json -->
  <xsl:template match="wix:Component[wix:File[contains(@Source, '\appsettings.Development.json')]]" />

</xsl:stylesheet>
