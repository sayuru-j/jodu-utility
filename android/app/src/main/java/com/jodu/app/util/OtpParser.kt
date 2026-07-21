package com.jodu.app.util

import java.util.regex.Pattern

object OtpParser {
    private val codePattern = Pattern.compile("\\b(\\d{4,8})\\b")
    private val keywords = listOf("code", "otp", "verification", "verify", "passcode", "pin")

    fun extract(title: String?, text: String?): String? {
        val body = listOfNotNull(title, text).joinToString(" ").trim()
        if (body.isEmpty()) return null
        val lower = body.lowercase()
        if (keywords.none { lower.contains(it) }) return null
        val matcher = codePattern.matcher(body)
        return if (matcher.find()) matcher.group(1) else null
    }
}
