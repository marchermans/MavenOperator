package io.mavenoperator.fixture;

/**
 * Trivial class — just needs to be compilable and publishable.
 * The E2E test verifies the .jar can be published to and resolved from
 * the operator-managed Hosted repository.
 */
public final class Widget {
    private Widget() {}

    public static String describe() {
        return "Widget from the Gradle Operator E2E fixture v1.0.0";
    }
}

